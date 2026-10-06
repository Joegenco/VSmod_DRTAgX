using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace DRTAgX;

/// <summary>Current-frame moving-light depth atlas: one projection per first-person
/// hand, or six faces at one authored range for other omnidirectional lights.</summary>
internal sealed class MovingLightShadowRenderer : IDisposable
{
    private const int BaseSize=192,MaxLights=10,TextureUnitNumber=15;
    // Apply after quantization/capping so every render resolution is exactly
    // half the accepted overhaul size. Set to 1 to restore that size schedule.
    internal const int ResolutionDivisor=2;
    private const float FarRange=48f;
    internal static readonly (float x,float y,float z,float ux,float uy,float uz)[] Directions =
    { (1,0,0,0,-1,0),(-1,0,0,0,-1,0),(0,1,0,0,0,1),(0,-1,0,0,0,-1),(0,0,1,0,-1,0),(0,0,-1,0,-1,0) };
    internal readonly TerrainCasterBatch Casters=new();
    private readonly HeldProjectionFit _fit=new();
    private readonly ShadowGlState _state=new(false);
    private readonly int[] _slots=new int[16],_topIndices=new int[10],_handSources=new int[2],_kinds=new int[10];
    private readonly float[] _topScores=new float[10],_sourceRanges=new float[16],_ranges=new float[10];
    private readonly float[] _oldMvp=new float[16],_oldOrigin=new float[3],_oldCamera=new float[16];
    private readonly float[] _scratchLightMatrix=new float[16],_scratchMeshMatrix=new float[16],_scratchViewMatrix=new float[16],_scratchProjMatrix=new float[16];
    private readonly double[] _inverseWorldView=new double[16];
    private readonly Vec3d _renderOrigin=new(),_lightWorld=new();
    private int _depthAtlas,_framebuffer,_size,_capacity,_width,_height,_columns,_heldRows;
    private int _previousTexture15,_previousSampler15;
    private bool _pending,_warned;
    private IndexedBufferState _uniform5;
    private int _shadowProgram,_mvp,_origin,_atlas,_heldIndex,_camera;
    private int _deferredProgram,_countLocation,_mapLocation,_slotsLocation,_kindLocation,_rangesLocation,_atlasInfoLocation,_columnsLocation;
    internal int Projections { get; private set; }
    internal int SkippedFaces { get; private set; }
    internal int DrawGroups { get; private set; }
    internal double PrepareMilliseconds { get; private set; }
    internal int FaceResolution => _size;

    internal static float Reach(float[] c)=>Math.Min(48f,MathF.Sqrt(c[0]*c[0]+c[1]*c[1]+c[2]*c[2]));
    internal static float SelectionScore(float[] p,float[] c,MovingLightKind kind)
    {
        float value=c[0]+c[1]+c[2];
        if(kind is MovingLightKind.RightHand or MovingLightKind.LeftHand)
            return value;
        return value/(1f+.15f*MathF.Sqrt(Dist2(p[0],p[1],p[2])));
    }

    internal static bool Outranks(float score,MovingLightKind kind,float otherScore,MovingLightKind otherKind)
    {
        bool held=kind is MovingLightKind.RightHand or MovingLightKind.LeftHand;
        bool otherHeld=otherKind is MovingLightKind.RightHand or MovingLightKind.LeftHand;
        if(held!=otherHeld)return held;
        if(score!=otherScore)return score>otherScore;
        return kind==MovingLightKind.RightHand&&otherKind==MovingLightKind.LeftHand;
    }

    internal bool Render(ICoreClientAPI api,float[][] positions,float[][] colors,int count,
        int maxShadowLights,IShaderProgram deferred,MovingLightKind[] sourceKinds,bool firstPerson)
    {
        long start=Stopwatch.GetTimestamp();
        PrepareMilliseconds=0;
        Projections=SkippedFaces=DrawGroups=0;
        Array.Fill(_slots,-1);Array.Fill(_topIndices,-1);Array.Fill(_handSources,-1);
        Array.Clear(_topScores);Array.Clear(_kinds);Array.Clear(_ranges);
        int capacity=Math.Clamp(maxShadowLights,1,MaxLights);
        for(int i=0;i<count;++i)
        {
            var p=positions[i];var c=colors[i];_sourceRanges[i]=Reach(c);
            if(_sourceRanges[i]<=0)continue;
            float score=SelectionScore(p,c,sourceKinds[i]);if(!float.IsFinite(score)||score<.02f)continue;
            for(int rank=0;rank<capacity;++rank)
            {
                if(_topIndices[rank]>=0 && !Outranks(score,sourceKinds[i],_topScores[rank],sourceKinds[_topIndices[rank]]))continue;
                for(int n=capacity-1;n>rank;--n){_topIndices[n]=_topIndices[n-1];_topScores[n]=_topScores[n-1];}
                _topIndices[rank]=i;_topScores[rank]=score;break;
            }
        }
        int selected=0;
        for(int rank=0;rank<capacity&&_topIndices[rank]>=0;++rank)
        {
            int i=_topIndices[rank],slot=selected++;_slots[i]=slot;_ranges[slot]=_sourceRanges[i];
            if(firstPerson&&sourceKinds[i] is MovingLightKind.RightHand or MovingLightKind.LeftHand)
            {
                int h=sourceKinds[i]==MovingLightKind.RightHand?0:1;_handSources[h]=i;_kinds[slot]=h+1;
            }
        }
        if(selected==0||api.World is not ClientMain world||NativeShadowFields.Renderer(world) is not { } renderer||
            NativeShadowFields.Passes(renderer) is not { Length: > 5 } passes||passes[0]==null||
            NativeShadowFields.Atlases(renderer) is not { } atlases||
            api.Render.GetEngineShader(EnumShaderProgram.Chunkshadowmap) is not { } shader||shader.Disposed||shader.LoadError||!Ensure(api,capacity))
        {PushUniforms(deferred,0);return false;}
        // Resize failure can retain a smaller usable atlas. Never address a slot
        // outside that allocation; lighting for omitted slots remains unshadowed.
        selected=Math.Min(selected,_capacity);
        for(int i=0;i<count;++i)if(_slots[i]>=selected)_slots[i]=-1;
        for(int h=0;h<2;++h)if(_handSources[h]>=0&&_slots[_handSources[h]]<0)_handSources[h]=-1;
        _state.Capture();
        IShaderProgram? previous=api.Render.CurrentActiveShader;
        int oldAtlas=0,oldHeld=0;bool uniformsCaptured=false;
        try
        {
            if(_shadowProgram!=shader.ProgramId)
            {
                _shadowProgram=shader.ProgramId;_mvp=GL.GetUniformLocation(_shadowProgram,"mvpMatrix");
                _origin=GL.GetUniformLocation(_shadowProgram,"origin");_atlas=GL.GetUniformLocation(_shadowProgram,"tex2d");
                _heldIndex=GL.GetUniformLocation(_shadowProgram,"drtHeldProjectionIndex");_camera=GL.GetUniformLocation(_shadowProgram,"drtHeldCameraMatrix");
            }
            if(_mvp<0||_origin<0||_atlas<0||_heldIndex<0||_camera<0)throw new InvalidOperationException("Moving-light shadow shader interface unavailable.");
            GL.GetUniform(shader.ProgramId,_mvp,_oldMvp);GL.GetUniform(shader.ProgramId,_origin,_oldOrigin);
            GL.GetUniform(shader.ProgramId,_atlas,out oldAtlas);GL.GetUniform(shader.ProgramId,_heldIndex,out oldHeld);
            GL.GetUniform(shader.ProgramId,_camera,_oldCamera);uniformsCaptured=true;
            GetTerrainRenderOrigin(world.CurrentModelViewMatrixd,api.Render.CameraMatrixOriginf);
            MovingLightGpuProfile.Total.BeginShadows(MovingLightGpuProfile.Enabled);
            _fit.Fit(api,positions,_sourceRanges,_handSources,_size*6);
            if(_fit.Buffer!=0)GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,_fit.Buffer);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,_framebuffer);
            GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Less);GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.ScissorTest);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.PolygonOffsetFill);
            GL.ColorMask(false,false,false,false);
            // One attachment, one clear. Gutters and skipped maps also get clear
            // depth, preventing stale shadows when sources or views change.
            GL.ClearDepth(1.0);GL.Clear(ClearBufferMask.DepthBufferBit);
            shader.Use();GL.Uniform1(_atlas,0);GL.Uniform1(_heldIndex,-1);
            float[] camera=api.Render.CameraMatrixOriginf,projection=api.Render.CurrentProjectionMatrix;
            GL.UniformMatrix4(_camera,1,false,camera);
            Casters.Begin(passes,atlases);
            PrepareMilliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            int query=MovingLightGpuProfile.Render.Begin(MovingLightGpuProfile.Enabled);
            try
            {
                for(int i=0;i<count;++i)
                {
                    int slot=_slots[i];if(slot<0)continue;
                    GetWorldLightPosition(positions[i]);float range=_ranges[slot]+.5f;
                    Casters.Prepare(_renderOrigin,_lightWorld,range,camera);
                    int stride=_size+8,sx=slot%_columns*2*stride,sy=slot/_columns*3*stride;
                    if(_kinds[slot]>0)
                    {
                        // Hand projectors occupy a separate strip: triple their prior
                        // 2*face resolution without enlarging any cube face.
                        GL.Viewport(_columns*2*stride+4,slot*(_size*6+8)+4,_size*6,_size*6);GL.Uniform1(_heldIndex,_kinds[slot]-1);
                        // Empty GPU fits set clip.w=0 in the vertex shader. No
                        // fragment work or depth is produced, without readback.
                        DrawGroups+=Casters.Draw(-1);Projections++;continue;
                    }
                    GL.Uniform1(_heldIndex,-1);
                    for(int face=0;face<6;++face)
                    {
                        if(!FaceMayContribute(positions[i],face,range,projection)){SkippedFaces++;continue;}
                        GL.Viewport(sx+(face%2)*stride+4,sy+(face/2)*stride+4,_size,_size);
                        MakeLightMatrix(positions[i],Directions[face],range,_scratchViewMatrix,_scratchProjMatrix,_scratchLightMatrix);
                        Multiply(_scratchLightMatrix,camera,_scratchMeshMatrix);GL.UniformMatrix4(_mvp,1,false,_scratchMeshMatrix);
                        DrawGroups+=Casters.Draw(face);Projections++;
                    }
                }
            }
            finally{MovingLightGpuProfile.Render.End(query);Casters.End();}
        }
        catch(Exception ex)
        {
            if(!_warned){api.Logger.Warning("[DRT AgX] Moving-light shadows unavailable; retaining unshadowed lighting: {0}",ex.Message);_warned=true;}
            selected=0;
        }
        finally
        {
            if(uniformsCaptured)
            {
                GL.UseProgram(shader.ProgramId);
                GL.UniformMatrix4(_mvp,1,false,_oldMvp);GL.Uniform3(_origin,_oldOrigin[0],_oldOrigin[1],_oldOrigin[2]);
                GL.Uniform1(_atlas,oldAtlas);GL.Uniform1(_heldIndex,oldHeld);GL.UniformMatrix4(_camera,1,false,_oldCamera);
            }
            MovingLightGpuProfile.Total.EndShadows();
            shader.Stop();previous?.Use();_state.Dispose();
        }
        PushUniforms(deferred,selected);return selected>0;
    }

    // Legacy static callers retain their signature; source-level batches below
    // are shared by both renderers without changing the native terrain ABI.
    private static float Dist2(float x,float y,float z)=>x*x+y*y+z*z;
    private void GetTerrainRenderOrigin(double[] worldView,float[] originView)
    {
        Mat4d.Invert(_inverseWorldView,worldView);
        double x=originView[12],y=originView[13],z=originView[14];
        _renderOrigin.Set(_inverseWorldView[0]*x+_inverseWorldView[4]*y+_inverseWorldView[8]*z+_inverseWorldView[12],
            _inverseWorldView[1]*x+_inverseWorldView[5]*y+_inverseWorldView[9]*z+_inverseWorldView[13],
            _inverseWorldView[2]*x+_inverseWorldView[6]*y+_inverseWorldView[10]*z+_inverseWorldView[14]);
    }
    private void GetWorldLightPosition(float[] eye)
    {
        double x=eye[0],y=eye[1],z=eye[2];
        _lightWorld.Set(_inverseWorldView[0]*x+_inverseWorldView[4]*y+_inverseWorldView[8]*z+_inverseWorldView[12],
            _inverseWorldView[1]*x+_inverseWorldView[5]*y+_inverseWorldView[9]*z+_inverseWorldView[13],
            _inverseWorldView[2]*x+_inverseWorldView[6]*y+_inverseWorldView[10]*z+_inverseWorldView[14]);
    }
    private static bool FaceMayContribute(float[] light, int face, float range, float[] projection)
    {
        // Only standard perspective matrices are eligible for this cull. The
        // origin plus four far corners enclose every receiver in camera view
        // within `range` of the light. If one face-cone plane excludes all five
        // points, no visible receiver can sample that shadow face.
        if (projection.Length < 16 || projection[0] <= 0f || projection[5] <= 0f ||
            Math.Abs(projection[11] + 1f) > 0.001f || Math.Abs(projection[15]) > 0.001f)
            return true;
        float farDepth = range + MathF.Sqrt(Dist2(light[0], light[1], light[2])) + 1f;
        if (!float.IsFinite(farDepth) || !float.IsFinite(projection[8]) || !float.IsFinite(projection[9]))
            return true;
        for (int plane = 0; plane < 5; plane++)
        {
            // The receiver can move by its normal bias and PCF footprint after
            // face selection. Keep a world-space guard band when culling layers.
            bool outside = FacePlaneValue(-light[0], -light[1], -light[2], face, plane) < -1.5f;
            if (!outside) continue;
            for (int y = -1; y <= 1 && outside; y += 2)
                for (int x = -1; x <= 1; x += 2)
                {
                    float px = farDepth * (x + projection[8]) / projection[0] - light[0];
                    float py = farDepth * (y + projection[9]) / projection[5] - light[1];
                    if (FacePlaneValue(px, py, -farDepth - light[2], face, plane) >= -1.5f)
                    {
                        outside = false;
                        break;
                    }
                }
            if (outside) return false;
        }
        return true;
    }

    private static float FacePlaneValue(float x, float y, float z, int face, int plane)
    {
        float forward = face switch { 0 => x, 1 => -x, 2 => y, 3 => -y, 4 => z, _ => -z };
        float sideA = face < 2 ? y : x;
        float sideB = face < 4 ? z : y;
        // 0.90 is wider than the actual 0.94 projection, with extra safety
        // for receiver bias, PCF taps, and camera projection jitter.
        return plane switch
        {
            0 => forward,
            1 => forward + 0.90f * sideA,
            2 => forward - 0.90f * sideA,
            3 => forward + 0.90f * sideB,
            _ => forward - 0.90f * sideB
        };
    }

    private static int GetFaceSize(ICoreClientAPI api)
    {
        // Final presentation pixels determine shadow quality. Internal terrain
        // resolution/SSAA changes do not apply another scale to this budget.
        return FaceSizeForDimensions(api.Render.FrameWidth,api.Render.FrameHeight);
    }

    internal static int FaceSizeForDimensions(int width,int height)
    {
        if(width<=0||height<=0)return BaseSize/ResolutionDivisor;
        // Linear map resolution follows sqrt(rendered pixel count), calibrated
        // to 192 cube pixels at 1080p before the reversible divisor. Held
        // projectors use six times the resulting cube size. Sixteen-
        // pixel steps and 48..768 bounds are divided along with the map size.
        double scale=Math.Sqrt((double)width*height/(1920.0*1080.0));
        return Math.Clamp((int)Math.Ceiling(BaseSize*scale/16.0)*16,48,768)/ResolutionDivisor;
    }

    internal static void MakeLightMatrix(float[] eye, (float x, float y, float z, float ux, float uy, float uz) d, float far, float[] view, float[] proj, float[] result)
    {
        float rx = d.uy * d.z - d.uz * d.y, ry = d.uz * d.x - d.ux * d.z,
            rz = d.ux * d.y - d.uy * d.x;
        float ux = d.y * rz - d.z * ry, uy = d.z * rx - d.x * rz,
            uz = d.x * ry - d.y * rx;
        view[0] = rx; view[1] = ux; view[2] = -d.x; view[3] = 0f;
        view[4] = ry; view[5] = uy; view[6] = -d.y; view[7] = 0f;
        view[8] = rz; view[9] = uz; view[10] = -d.z; view[11] = 0f;
        view[12] = -rx * eye[0] - ry * eye[1] - rz * eye[2];
        view[13] = -ux * eye[0] - uy * eye[1] - uz * eye[2];
        view[14] = d.x * eye[0] + d.y * eye[1] + d.z * eye[2];
        view[15] = 1f;
        const float near = 0.1f;
        float nf = near - far;
        proj[0] = 0.94f; proj[1] = 0f; proj[2] = 0f; proj[3] = 0f;
        proj[4] = 0f; proj[5] = 0.94f; proj[6] = 0f; proj[7] = 0f;
        proj[8] = 0f; proj[9] = 0f; proj[10] = (far + near) / nf; proj[11] = -1f;
        proj[12] = 0f; proj[13] = 0f; proj[14] = 2f * far * near / nf; proj[15] = 0f;
        Multiply(proj, view, result);
    }

    internal static float[] MakeLightMatrix(float[] eye, (float x, float y, float z, float ux, float uy, float uz) d, float far)
    {
        float[] view = new float[16];
        float[] proj = new float[16];
        float[] result = new float[16];
        MakeLightMatrix(eye, d, far, view, proj, result);
        return result;
    }

    internal static void Multiply(float[] a, float[] b, float[] result)
    {
        for (int c = 0; c < 4; c++)
        {
            int c4 = c * 4;
            for (int r = 0; r < 4; r++)
            {
                result[c4 + r] = a[r] * b[c4] +
                                 a[4 + r] * b[c4 + 1] +
                                 a[8 + r] * b[c4 + 2] +
                                 a[12 + r] * b[c4 + 3];
            }
        }
    }

    private static float[] Multiply(float[] a, float[] b)
    {
        float[] result = new float[16];
        Multiply(a, b, result);
        return result;
    }

    private bool Ensure(ICoreClientAPI api,int capacity)
    {
        // Both cube faces and the six-times held projector use the same frame policy.
        int size=FrameQuality.Current.MovingFace(GetFaceSize(api));
        // Reserve high-resolution storage only for selected first-person hands;
        // dropped/API lights keep the compact cube-only allocation and clear.
        int heldRows=(_handSources[0]>=0?1:0)+(_handSources[1]>=0?1:0);
        if(_depthAtlas!=0&&_size==size&&_capacity==capacity&&_heldRows==heldRows)return true;
        ReleaseTextureBinding();
        int restore=GL.GetInteger(GetPName.TextureBinding2D),draw=GL.GetInteger(GetPName.DrawFramebufferBinding),read=GL.GetInteger(GetPName.ReadFramebufferBinding);
        int unpack=GL.GetInteger(GetPName.PixelUnpackBufferBinding);
        int depth=0,fbo=0,columns=Math.Min(capacity,5),width=columns*2*(size+8)+(heldRows>0?size*6+8:0),
            height=Math.Max(((capacity+columns-1)/columns)*3*(size+8),heldRows*(size*6+8));
        try
        {
            // A null pixel pointer means PBO offset zero when an unpack buffer
            // is bound. Atlas allocation supplies no pixels and must bind zero.
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,0);
            depth=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,depth);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,width,height,0,PixelFormat.DepthComponent,PixelType.UnsignedInt,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareMode,(int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureCompareFunc,(int)DepthFunction.Lequal);
            fbo=GL.GenFramebuffer();GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,depth,0);
            GL.DrawBuffer(DrawBufferMode.None);GL.ReadBuffer(ReadBufferMode.None);
            if(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new InvalidOperationException("Moving-light depth framebuffer incomplete.");
            if(restore==_depthAtlas&&_depthAtlas!=0)restore=depth;
            if(draw==_framebuffer&&_framebuffer!=0)draw=fbo;if(read==_framebuffer&&_framebuffer!=0)read=fbo;
            if(_depthAtlas!=0)GL.DeleteTexture(_depthAtlas);if(_framebuffer!=0)GL.DeleteFramebuffer(_framebuffer);
            _depthAtlas=depth;_framebuffer=fbo;_size=size;_capacity=capacity;_columns=columns;_width=width;_height=height;_heldRows=heldRows;
            depth=fbo=0;return true;
        }
        catch(Exception ex){if(!_warned){api.Logger.Warning("[DRT AgX] Moving-light atlas allocation failed: {0}",ex.Message);_warned=true;}return _depthAtlas!=0&&_heldRows>=heldRows;}
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,draw);GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,read);
            GL.BindTexture(TextureTarget.Texture2D,restore);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer,unpack);
            if(depth!=0)GL.DeleteTexture(depth);if(fbo!=0)GL.DeleteFramebuffer(fbo);
        }
    }
    private void PushUniforms(IShaderProgram deferred,int count)
    {
        ReleaseTextureBinding();int previous=GL.GetInteger(GetPName.CurrentProgram);GL.UseProgram(deferred.ProgramId);
        if(_deferredProgram!=deferred.ProgramId)
        {
            _deferredProgram=deferred.ProgramId;_countLocation=GL.GetUniformLocation(_deferredProgram,"drtShadowCount");
            _mapLocation=GL.GetUniformLocation(_deferredProgram,"drtShadowMaps");_slotsLocation=GL.GetUniformLocation(_deferredProgram,"drtShadowSlot[0]");
            _kindLocation=GL.GetUniformLocation(_deferredProgram,"drtShadowKind[0]");_rangesLocation=GL.GetUniformLocation(_deferredProgram,"drtShadowRange[0]");
            _atlasInfoLocation=GL.GetUniformLocation(_deferredProgram,"drtMovingAtlasInfo");_columnsLocation=GL.GetUniformLocation(_deferredProgram,"drtMovingAtlasColumns");
        }
        if(count==0)Array.Fill(_slots,-1);
        GL.Uniform1(_countLocation,count);GL.Uniform1(_mapLocation,TextureUnitNumber);GL.Uniform1(_slotsLocation,16,_slots);
        GL.Uniform1(_kindLocation,10,_kinds);GL.Uniform1(_rangesLocation,10,_ranges);
        GL.Uniform4(_atlasInfoLocation,(float)_width,(float)_height,(float)_size,(float)(_size+8));GL.Uniform1(_columnsLocation,_columns);
        if(count>0)
        {
            int active=GL.GetInteger(GetPName.ActiveTexture);GL.ActiveTexture(TextureUnit.Texture15);
            _previousTexture15=GL.GetInteger(GetPName.TextureBinding2D);_previousSampler15=GL.GetInteger(GetPName.SamplerBinding);
            _uniform5=IndexedBufferState.Capture(BufferRangeTarget.UniformBuffer,5);
            GL.BindTexture(TextureTarget.Texture2D,_depthAtlas);GL.BindSampler(15,0);
            int generic=GL.GetInteger(GetPName.UniformBufferBinding);
            if(_fit.Buffer!=0)GL.BindBufferBase(BufferRangeTarget.UniformBuffer,5,_fit.Buffer);
            GL.BindBuffer(BufferTarget.UniformBuffer,generic);
            GL.ActiveTexture((TextureUnit)active);_pending=true;
        }
        GL.UseProgram(previous);
    }
    internal void Disable(IShaderProgram deferred)=>PushUniforms(deferred,0);
    internal void ReleaseTextureBinding()
    {
        if(!_pending)return;int active=GL.GetInteger(GetPName.ActiveTexture);GL.ActiveTexture(TextureUnit.Texture15);
        GL.BindTexture(TextureTarget.Texture2D,_previousTexture15);GL.BindSampler(15,_previousSampler15);
        int generic=GL.GetInteger(GetPName.UniformBufferBinding);_uniform5.Restore();GL.BindBuffer(BufferTarget.UniformBuffer,generic);
        GL.ActiveTexture((TextureUnit)active);_pending=false;
    }
    internal void Reload(){_shadowProgram=_deferredProgram=0;_fit.Reload();_warned=false;}
    public void Dispose(){ReleaseTextureBinding();Casters.End();_fit.Dispose();if(_depthAtlas!=0)GL.DeleteTexture(_depthAtlas);if(_framebuffer!=0)GL.DeleteFramebuffer(_framebuffer);_depthAtlas=_framebuffer=_size=_capacity=0;}
}
