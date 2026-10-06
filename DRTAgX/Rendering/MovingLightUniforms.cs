using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace DRTAgX;

// Per-program locations and fixed upload buffers. Shader reload invalidates
// locations even when the driver recycles a program ID.
internal sealed class MovingLightUniforms
{
    private int _native,_deferred,_quantity,_count,_projection,_positions,_colors,_radiance;
    private readonly int[] _nativePositions=new int[16],_nativeColors=new int[16];
    private readonly float[] _positionData=new float[48],_colorData=new float[48],_prepared=new float[64];
    internal void Reload(){_native=_deferred=0;}
    internal int ReadNative(IShaderProgram shader,float[][] positions,float[][] colors)
    {
        if(_native!=shader.ProgramId)
        {
            _native=shader.ProgramId;_quantity=GL.GetUniformLocation(_native,"pointLightQuantity");
            for(int i=0;i<16;++i){_nativePositions[i]=GL.GetUniformLocation(_native,$"pointLights[{i}]");_nativeColors[i]=GL.GetUniformLocation(_native,$"pointLightColors[{i}]");}
        }
        if(_quantity<0)return 0;
        GL.GetUniform(_native,_quantity,out int count);count=Math.Clamp(count,0,16);
        for(int i=0;i<count;++i)
        {
            if(_nativePositions[i]<0||_nativeColors[i]<0)return i;
            GL.GetUniform(_native,_nativePositions[i],positions[i]);GL.GetUniform(_native,_nativeColors[i],colors[i]);
        }
        return count;
    }
    internal void Publish(IShaderProgram shader,int count,float[][] positions,float[][] colors,float[] projection)
    {
        if(_deferred!=shader.ProgramId)
        {
            _deferred=shader.ProgramId;_count=GL.GetUniformLocation(_deferred,"drtPointCount");_projection=GL.GetUniformLocation(_deferred,"drtProjectionMatrix");
            _positions=GL.GetUniformLocation(_deferred,"drtPointPos[0]");_colors=GL.GetUniformLocation(_deferred,"drtPointColor[0]");_radiance=GL.GetUniformLocation(_deferred,"drtPointRadiance[0]");
        }
        for(int i=0;i<count;++i)
        {
            Array.Copy(positions[i],0,_positionData,i*3,3);Array.Copy(colors[i],0,_colorData,i*3,3);
            float[] c=colors[i];float peak=Math.Max(c[0],Math.Max(c[1],c[2]));
            for(int j=0;j<3;++j)_prepared[i*4+j]=peak>1e-6f?Math.Max(c[j]/peak,0):0;
            _prepared[i*4+3]=MathF.Sqrt(c[0]*c[0]+c[1]*c[1]+c[2]*c[2]);
        }
        int previous=GL.GetInteger(GetPName.CurrentProgram);GL.UseProgram(_deferred);
        try
        {
            GL.Uniform1(_count,count);GL.UniformMatrix4(_projection,1,false,projection);
            if(count>0){GL.Uniform3(_positions,count,_positionData);GL.Uniform3(_colors,count,_colorData);GL.Uniform4(_radiance,count,_prepared);}
        }
        finally{GL.UseProgram(previous);}
    }
}
