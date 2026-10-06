using System;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DRTAgX;

// Current-frame GPU projection fitting. Binding 5 is a UBO, independent of the
// placed-light SSBO at index 5. Temporary SSBOs 6/7 and texture 13 are restored.
internal sealed class HeldProjectionFit : IDisposable
{
    private int _tilesProgram,_finalProgram,_tilesBuffer,_tileBytes;
    private bool _failed;
    private readonly float[] _inverse=new float[16],_output=new float[40],_lights=new float[8];
    private readonly Locations[] _locations=new Locations[2];
    private sealed class Locations(int p)
    {
        internal readonly int Inverse=GL.GetUniformLocation(p,"inverseProjection"),Lights=GL.GetUniformLocation(p,"handLight[0]"),
            Dimensions=GL.GetUniformLocation(p,"dimensions"),Width=GL.GetUniformLocation(p,"tileWidth"),Count=GL.GetUniformLocation(p,"tileCount"),
            Size=GL.GetUniformLocation(p,"heldSize"),Depth=GL.GetUniformLocation(p,"terrainDepth");
    }
    internal int Buffer { get; private set; }

    internal void Fit(ICoreClientAPI api,float[][] positions,float[] ranges,int[] handSources,int size)
    {
        // Cube-only frames do not consume held matrices. Keep a valid UBO for
        // the shared shader interface, without repeating fitting state queries.
        if(handSources[0]<0&&handSources[1]<0&&Buffer!=0)return;
        int generic=GL.GetInteger(GetPName.ShaderStorageBufferBinding),program=GL.GetInteger(GetPName.CurrentProgram);
        var old6=IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer,6);
        var old7=IndexedBufferState.Capture(BufferRangeTarget.ShaderStorageBuffer,7);
        int active=GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture13);
        int texture=GL.GetInteger(GetPName.TextureBinding2D),sampler=GL.GetInteger(GetPName.SamplerBinding);
        try
        {
            if(Buffer==0)Buffer=GL.GenBuffer();
            Array.Clear(_lights);Array.Clear(_output);
            float[] projection=api.Render.CurrentProjectionMatrix;
            for(int h=0;h<2;++h)
            {
                int i=handSources[h];if(i<0)continue;
                Array.Copy(positions[i],0,_lights,h*4,3);_lights[h*4+3]=ranges[i];
                Fallback(projection,positions[i],ranges[i],_output,h*16);
                _output[32+h*4]=1;
            }
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,Buffer);
            // Orphan previous frame's GPU-written block before uploading fallbacks.
            GL.BufferData(BufferTarget.ShaderStorageBuffer,160,_output,BufferUsageHint.StreamDraw);
            if(handSources[0]<0&&handSources[1]<0)return;
            var buffers=api.Render.FrameBuffers;
            if(_failed||buffers.Count==0||buffers[0].Disposed||buffers[0].DepthTextureId<=0||
                buffers[0].Width!=api.Render.FrameWidth||buffers[0].Height!=api.Render.FrameHeight||
                Mat4f.Invert(_inverse,projection)==null)return;
            Ensure(api);
            // 64x64 tiles amortize workgroup barriers and final reduction bandwidth
            // while still inspecting every depth pixel for close-wall coverage.
            int width=(api.Render.FrameWidth+63)/64,tiles=width*((api.Render.FrameHeight+63)/64),bytes=tiles*64;
            if(_tilesBuffer==0)_tilesBuffer=GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,_tilesBuffer);
            if(bytes!=_tileBytes){GL.BufferData(BufferTarget.ShaderStorageBuffer,bytes,IntPtr.Zero,BufferUsageHint.DynamicDraw);_tileBytes=bytes;}
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,6,_tilesBuffer);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer,7,Buffer);
            GL.BindSampler(13,0);GL.BindTexture(TextureTarget.Texture2D,buffers[0].DepthTextureId);
            int query=MovingLightGpuProfile.Fit.Begin(MovingLightGpuProfile.Enabled);
            try
            {
                for(int pass=0;pass<2;++pass)
                {
                    int p=pass==0?_tilesProgram:_finalProgram;var loc=_locations[pass];GL.UseProgram(p);
                    GL.Uniform4(loc.Lights,2,_lights);GL.Uniform1(loc.Count,tiles);
                    if(pass==0){GL.Uniform1(loc.Depth,13);GL.UniformMatrix4(loc.Inverse,1,false,_inverse);GL.Uniform2(loc.Dimensions,api.Render.FrameWidth,api.Render.FrameHeight);GL.Uniform1(loc.Width,width);}
                    else GL.Uniform1(loc.Size,size);
                    GL.DispatchCompute(pass==0?Math.Min(tiles,65535):2,pass==0?(tiles+65534)/65535:1,1);
                    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit|MemoryBarrierFlags.UniformBarrierBit);
                }
            }
            finally{MovingLightGpuProfile.Fit.End(query);}
        }
        catch(Exception ex)
        {
            // The CPU fallback was uploaded before dispatch; failures retain
            // one conservative projection, never the previous frame's fit.
            if(!_failed)api.Logger.Warning("[DRT AgX] Held projection fit unavailable; retaining conservative projection: {0}",ex.Message);
            _failed=true;
        }
        finally
        {
            old6.Restore();old7.Restore();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer,generic);GL.BindTexture(TextureTarget.Texture2D,texture);GL.BindSampler(13,sampler);
            GL.ActiveTexture((TextureUnit)active);GL.UseProgram(program);
        }
    }

    internal static void Fallback(float[] camera,float[] light,float range,float[] result,int offset)
    {
        // Every camera-frustum receiver at depth >= its near plane is enclosed.
        // A camera-plane light only adds the bounded lateral offset / nearDepth.
        float candidateNear=camera[14]/(camera[10]-1f);
        float near=float.IsFinite(candidateNear)?Math.Max(.005f,candidateNear):.005f;
        float sx=float.IsFinite(camera[0])&&camera[0]>1e-6f?1f/camera[0]:10f;
        float sy=float.IsFinite(camera[5])&&camera[5]>1e-6f?1f/camera[5]:10f;
        float oxCamera=float.IsFinite(camera[8])?camera[8]:0,oyCamera=float.IsFinite(camera[9])?camera[9]:0;
        float minX=(-1+oxCamera)*sx-Math.Max(light[0],0)/near;
        float maxX=(1+oxCamera)*sx-Math.Min(light[0],0)/near;
        float minY=(-1+oyCamera)*sy-Math.Max(light[1],0)/near;
        float maxY=(1+oyCamera)*sy-Math.Min(light[1],0)/near;
        float dx=(maxX-minX)*1.2f,dy=(maxY-minY)*1.2f,cx=(minX+maxX)*.5f,cy=(minY+maxY)*.5f;
        float px=2f/dx,py=2f/dy,ox=2f*cx/dx,oy=2f*cy/dy,far=range+.5f;
        const float n=.005f;
        Array.Clear(result,offset,16);
        result[offset]=px;result[offset+5]=py;result[offset+8]=ox;result[offset+9]=oy;
        result[offset+10]=(far+n)/(n-far);result[offset+11]=-1;
        result[offset+12]=-px*light[0]-ox*light[2];result[offset+13]=-py*light[1]-oy*light[2];
        result[offset+14]=2f*far*n/(n-far)-result[offset+10]*light[2];result[offset+15]=light[2];
    }
    private void Ensure(ICoreClientAPI api)
    {
        if(_tilesProgram!=0&&_finalProgram!=0)return;
        string source=Encoding.UTF8.GetString(api.Assets.Get(new AssetLocation("drtagx","shaders/movinglightfit.csh")).Data);
        _tilesProgram=Compile(source);_finalProgram=Compile(source.Replace("#version 430 core","#version 430 core\n#define DRT_FINAL_FIT"));
        _locations[0]=new Locations(_tilesProgram);_locations[1]=new Locations(_finalProgram);
    }
    private static int Compile(string source)
    {
        int shader=GL.CreateShader(ShaderType.ComputeShader),p=0;
        try
        {
            GL.ShaderSource(shader,source);GL.CompileShader(shader);GL.GetShader(shader,ShaderParameter.CompileStatus,out int ok);
            if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            p=GL.CreateProgram();GL.AttachShader(p,shader);GL.LinkProgram(p);GL.GetProgram(p,GetProgramParameterName.LinkStatus,out ok);
            if(ok==0)throw new InvalidOperationException(GL.GetProgramInfoLog(p));
            int result=p;p=0;return result;
        }
        finally{GL.DeleteShader(shader);if(p!=0)GL.DeleteProgram(p);}
    }
    internal void Reload(){if(_tilesProgram!=0)GL.DeleteProgram(_tilesProgram);if(_finalProgram!=0)GL.DeleteProgram(_finalProgram);_tilesProgram=_finalProgram=0;_failed=false;}
    public void Dispose(){Reload();if(Buffer!=0)GL.DeleteBuffer(Buffer);if(_tilesBuffer!=0)GL.DeleteBuffer(_tilesBuffer);Buffer=_tilesBuffer=_tileBytes=0;}
}

