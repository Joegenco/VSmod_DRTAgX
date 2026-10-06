using System.Diagnostics;
using Vintagestory.API.Client;

namespace DRTAgX;

// Alt+9 opts in. Combined timing sums paired fit/render and deferred intervals
// from the same frame, excluding intervening placed-light preparation.
internal static class MovingLightGpuProfile
{
    internal static bool Enabled { get; private set; }
    internal static readonly GpuPassTimer Fit=new(),Render=new(),Relight=new();
    internal static readonly MovingLightFrameTimer Total=new();
    private static long _next;
    internal static void Toggle(ICoreClientAPI api){if(!Vintagestory.Client.NoObf.ClientSettings.DeveloperMode)return;Enabled=!Enabled;Reload();api.Logger.Notification("[DRT AgX] Moving-light profiling {0}; Alt+9 toggles, results in client-main.log.",Enabled?"enabled":"disabled");}
    // Called on the render thread when vanilla developer mode is revoked.
    internal static void Disable(){if(!Enabled)return;Enabled=false;Reload();}
    internal static void Reload(){Fit.ClearSamples();Render.ClearSamples();Relight.ClearSamples();Total.Clear();_next=0;}
    internal static void Frame(ICoreClientAPI api,MovingLightKind[] kinds,int count,MovingLightShadowRenderer maps,bool shadows,int budget)
    {
        if(!Enabled)return;
        long now=Stopwatch.GetTimestamp();if(now<_next)return;_next=now+3*Stopwatch.Frequency;
        Fit.Poll();Render.Poll();Relight.Poll();Total.Poll();
        int right=0,left=0,entity=0,other=0;
        for(int i=0;i<count;++i)switch(kinds[i]){case MovingLightKind.RightHand:right++;break;case MovingLightKind.LeftHand:left++;break;case MovingLightKind.Entity:entity++;break;default:other++;break;}
        var fit=Fit.Statistics();var render=Render.Statistics();var relight=Relight.Statistics();var total=Total.Statistics();
        api.Logger.Notification("[DRT AgX Moving GPU] {0}x{1}, shadows={2}, budget={3}, right/left/entity/api={4}/{5}/{6}/{7}, projections={8}, skippedFaces={9}, groups={10}, CPU prepare={11:F3} ms; fit median/p95={12:F3}/{13:F3}, render={14:F3}/{15:F3}, relight={16:F3}/{17:F3}, paired combined={18:F3}/{19:F3} ms",
            api.Render.FrameWidth,api.Render.FrameHeight,shadows,budget,right,left,entity,other,shadows?maps.Projections:0,shadows?maps.SkippedFaces:0,shadows?maps.DrawGroups:0,shadows?maps.PrepareMilliseconds:0,
            fit.Median,fit.P95,render.Median,render.P95,relight.Median,relight.P95,total.Median,total.P95);
        api.Logger.Notification("[DRT AgX Moving GPU] Actual atlas face/held resolution={0}/{1} pixels.",maps.FaceResolution,maps.FaceResolution*6);
    }
    internal static void Dispose(){Enabled=false;Fit.Dispose();Render.Dispose();Relight.Dispose();Total.Dispose();}
}
