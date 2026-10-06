using System;
using System.Reflection;
using DRTAgX;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

internal static class SurfaceBindingsProbe
{
    internal static void Run()
    {
        string vertex = "#version 430 core\nvoid main(){gl_Position=vec4(0.0);}";
        string fragment = """
            #version 430 core
            uniform vec2 drtSunlightBounds;
            uniform vec2 drtPlacedCalibration[32];
            uniform vec3 rgbaAmbientIn;
            uniform vec3 drtSkyColor;
            uniform vec3 drtSunGridCameraPhase;
            uniform int level;
            out vec4 color;
            void main() {
                color=vec4(drtSunlightBounds + drtPlacedCalibration[level], 0, 0) +
                    vec4(rgbaAmbientIn + drtSkyColor + drtSunGridCameraPhase, 0);
            }
            """;
        int target = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int native = ProbeShader.Program((ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment));
        int previous = GL.GetInteger(GetPName.CurrentProgram);
        try
        {
            IShaderProgram Target(int program) => SurfaceApiProxy.Make<IShaderProgram>((method, _) => method.Name switch
            {
                "get_ProgramId" => program,
                "get_Disposed" or "get_LoadError" => false,
                _ => throw new NotSupportedException(method.Name)
            });
            float[] sun = [0.03f, 0.2f, 0.81f], block = new float[32];
            for (int i = 0; i < 32; ++i) block[i] = i / 31f;
            IClientWorldAccessor World() => SurfaceApiProxy.Make<IClientWorldAccessor>((method, _) => method.Name switch
            {
                "get_SunLightLevels" => sun,
                "get_BlockLightLevels" => block,
                "get_SunBrightness" => 2,
                _ => throw new NotSupportedException(method.Name)
            });
            IClientWorldAccessor world = World();
            var api = SurfaceApiProxy.Make<ICoreClientAPI>((method, _) => method.Name switch
            {
                "get_World" => world,
                _ => throw new NotSupportedException(method.Name)
            });
            var bindings = new SurfaceLightBindings();
            var targetShader = Target(target);
            void Check(bool ok, string name) {
                if (!ok) throw new Exception(name);
                Console.WriteLine("PASS " + name);
            }
            float[] Read(int program, string name, int size) {
                float[] result = new float[size]; GL.GetUniform(program, GL.GetUniformLocation(program, name), result);
                return result;
            }
            bindings.Refresh(api); bindings.Bind(targetShader);
            float[] bounds = Read(target, "drtSunlightBounds", 2);
            Check(bounds[0] == sun[0] && bounds[1] == sun[2] && GL.GetInteger(GetPName.CurrentProgram) == previous,
                "production bindings use live bounds and restore incoming GL program");
            float[] calibration = new float[64]; SurfaceLightBindings.Calibrate(block, calibration);
            float[] actual = Read(target, "drtPlacedCalibration[14]", 2);
            Check(Math.Abs(actual[0]-calibration[28]) < 1e-6 && actual[1] == calibration[29], "production bindings upload direct/self calibration array");
            GL.UseProgram(native); GL.Uniform3(GL.GetUniformLocation(native, "rgbaAmbientIn"), 0.02f, 0.03f, 0.04f); GL.UseProgram(previous);
            bindings.PublishSky(Target(native), targetShader);
            actual = Read(target, "drtSkyColor", 3);
            Check(actual[0] == 0.02f && actual[1] == 0.03f && actual[2] == 0.04f &&
                GL.GetInteger(GetPName.CurrentProgram) == previous, "production sky publication captures native RGB and restores GL state");
            bindings.SetSunGridCamera(new Vec3d(512425.13836669928,113.69921875,-513009.2911987304));
            bindings.PublishSky(Target(native),targetShader);
            actual=Read(target,"drtSunGridCameraPhase",3);
            Check(Math.Abs(actual[0]-.13836669928f)<1e-7 && actual[1]==.69921875f &&
                Math.Abs(actual[2]-.7088012696f)<1e-7 && GL.GetInteger(GetPName.CurrentProgram)==previous,
                "sun grid binding retains double-precision world phase and GL program");
            bindings.PublishSky(null, targetShader);
            actual = Read(target, "drtSkyColor", 3);
            Check(actual[0] == 0 && actual[1] == 0 && actual[2] == 0, "missing native program clears stale sky radiance");
            world = World(); sun = [0, 0.3f, 0.6f];
            bindings.Refresh(api); bindings.Bind(targetShader);
            bounds = Read(target, "drtSunlightBounds", 2);
            Check(bounds[0] == 0 && bounds[1] == 0.6f, "new world/table refresh republishes scales");
            // Simulate reused native program identity after reload by poisoning its old uniform.
            GL.UseProgram(target); GL.Uniform2(GL.GetUniformLocation(target, "drtSunlightBounds"), 4f, 5f); GL.UseProgram(previous);
            bindings.Reset(); bindings.Refresh(api); bindings.Bind(targetShader);
            bounds = Read(target, "drtSunlightBounds", 2);
            Check(bounds[0] == 0 && bounds[1] == 0.6f && GL.GetInteger(GetPName.CurrentProgram) == previous,
                "reload invalidates location/upload caches even with reused program ID");
            Check(GL.GetError() == ErrorCode.NoError, "lighting lifecycle bindings produce no GL errors");
        }
        finally { GL.UseProgram(previous); GL.DeleteProgram(target); GL.DeleteProgram(native); }
    }
}

// Minimal interface adapters let the production binder read synthetic world
// tables and real linked programs without starting or mutating a game world.
public class SurfaceApiProxy : DispatchProxy
{
    private Func<MethodInfo, object[], object> _call;
    internal static T Make<T>(Func<MethodInfo, object[], object> call) where T : class
    {
        T proxy = Create<T, SurfaceApiProxy>();
        ((SurfaceApiProxy)(object)proxy)._call = call;
        return proxy;
    }
    protected override object Invoke(MethodInfo method, object[] args) => _call(method, args);
}
