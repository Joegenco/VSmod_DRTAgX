using System;
using DRTAgX;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

/// <summary>Known ordered overlays verify baseline substitution, compatibility and daily profile continuity.</summary>
internal static class AmbientInputsProbe
{
    internal static void Run()
    {
        AmbientModifier Color(float r, float g, float b, float weight) => new()
        { AmbientColor = new WeightedFloatArray { Value = [r,g,b], Weight = weight },
          SceneBrightness = new WeightedFloat { Value = 1, Weight = 0 } };
        var basis = Color(1,1,1,1); basis.SceneBrightness.Value = 1;
        var modifiers = new OrderedDictionary<string, AmbientModifier>();
        modifiers.Add("sunglow", Color(.9f,.6f,.3f,1));
        var night = Color(0,0,0,0); night.SceneBrightness.Value=.5f; night.SceneBrightness.Weight=1;
        modifiers.Add("night", night);
        var weather = Color(.2f,.2f,.2f,.25f); weather.SceneBrightness.Value=.8f; weather.SceneBrightness.Weight=.25f;
        modifiers.Add("weather", weather);
        modifiers.Add("unknown-mod", Color(.1f,.5f,.2f,.4f));
        var native = new Vec3f(.273125f,.2875f,.140875f);
        var api = SurfaceApiProxy.Make<IAmbientManager>((method,_) => method.Name switch
        {
            "get_Base" => basis, "get_CurrentModifiers" => modifiers,
            "get_BlendedAmbientColor" => native, "get_BlendedCloudDensity" => .4f,
            _ => throw new NotSupportedException(method.Name)
        });
        var frame = new float[AtmosphereRenderer.FrameFloatCount];
        AmbientSkyInputs.Capture(api,frame);
        Equal(frame[95],1,"native ordered replay verified");
        Equal(frame[92],.4275f,"weather/unknown weights retain their baseline coefficient");
        Equal(frame[96],.0665f,"ordered red overlay retained");
        Equal(frame[97],.2185f,"ordered green overlay retained");
        Equal(frame[98],.1045f,"ordered blue overlay retained");
        Equal(frame[103],.4f,"native cloud density retained");
        native.X += .01f; AmbientSkyInputs.Capture(api,frame);
        Equal(frame[95],0,"unreconstructed native provider falls back");
        native.X -= .01f;
        var water = Color(.07f,.28f,.25f,1); modifiers.Add("water",water);
        native.X=.07f*.575f; native.Y=.28f*.575f; native.Z=.25f*.575f;
        AmbientSkyInputs.Capture(api,frame);
        Equal(frame[95],1,"underwater ambient contract retained");
        Equal(frame[92],0,"full underwater color overrides environmental sky");
        Equal(frame[97],.28f*.95f,"underwater tint retains remaining scene overlays");
        modifiers.Remove("sunglow"); AmbientSkyInputs.Capture(api,frame);
        Equal(frame[95],0,"missing baseline producer falls back");

        var first = new float[AtmosphereRenderer.FrameFloatCount];
        var second = new float[first.Length];
        new AtmosphereSkyProfile().Capture(.005685588f,0,.2f,.016f,first);
        new AtmosphereSkyProfile().Capture(.005685588f,0,.2f,.016f,second);
        for(int i=108;i<112;i++) Equal(first[i],second[i],"native daily seed is deterministic");
        var other = new AtmosphereSkyProfile();
        other.Capture(.019f,0,.2f,.016f,second);
        Check(Math.Abs(first[108]-second[108])>.01f || Math.Abs(first[110]-second[110])>.01f,"different native days change scattering");
        other.Reset(); other.Capture(.019f,1,0,.016f,second);
        Equal(second[108],1,"noon aerosol reference retained"); Equal(second[110],1,"noon ozone reference retained");
        Equal(second[111],.35f,"noon spread reference retained");
        var smooth = new AtmosphereSkyProfile(); smooth.Capture(.005685588f,0,0,.016f,second);
        float previous=second[108]; smooth.Capture(.019f,0,0,.016f,second);
        Check(Math.Abs(second[108]-previous)<.005f,"daily profile changes smoothly");
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) smooth.Capture(.019f,0,0,.016f,second);
        Check(GC.GetAllocatedBytesForCurrentThread()==allocated,"profile hot path allocates zero bytes");
        Console.WriteLine("PASS native ambient overlay replay, underwater/provider fallback and deterministic smooth daily profiles");
    }

    private static void Equal(float actual,float expected,string name) => Check(Math.Abs(actual-expected)<.00001f,name);
    private static void Check(bool ok,string name) { if(!ok) throw new Exception(name); }
}
