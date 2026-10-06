using System;
using DRTAgX;
using Vintagestory.API.MathTools;

if (args.Length > 0 && args[0] is "--cache-audit" or "--cache-regressions") {
    SourceCacheProbe.Run(args[0] == "--cache-regressions");
    return;
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

// Full-cache policy retains equal-tier contributors and deterministically
// reclaims offscreen, then lower-priority maps by their last recorded hit.
var residents = new PlacedLightPolicy.Resident[128];
for (int i = 0; i < residents.Length; i++) residents[i] = new(1, 20, false, i, 0, 0);
Check(PlacedLightPolicy.ChooseVictim(residents, 1) == -1, "equal-tier full cache stays stable");
residents[31] = new(2, 5, false, 31, 0, 0);
residents[127] = new(3, 19, false, 127, 0, 0);
Check(PlacedLightPolicy.ChooseVictim(residents, 1) == 127, "offscreen map reclaimed before visible far map");
residents[127] = new(3, 19, true, 127, 0, 0);
Check(PlacedLightPolicy.ChooseVictim(residents, 1) == 31, "protected slot is never a victim");
residents[32] = new(2, 4, false, 32, 0, 0);
Check(PlacedLightPolicy.ChooseVictim(residents, 1) == 32, "oldest hit breaks victim tier ties");
residents[31] = new(2, 4, false, 31, 0, 0);
Check(PlacedLightPolicy.ChooseVictim(residents, 1) == 31, "coordinates break exact victim ties");
Check(PlacedLightPolicy.ChooseVictim(residents, 0) == 31, "center candidate outranks nearby peripheral maps");
Check(PlacedLightPolicy.CanAdmit(10000, false, 100, 100), "free slot bypasses far delay");
Check(PlacedLightPolicy.CanAdmit(4096, true, 100, 100), "64-block boundary is immediate");
Check(!PlacedLightPolicy.CanAdmit(4097, true, 100, 101.999), "constrained far light waits two seconds");
Check(PlacedLightPolicy.CanAdmit(4097, true, 100, 102), "far admission becomes eligible at two seconds");
Check(!PlacedLightPolicy.Expired(double.NegativeInfinity, 100, 109.999), "completed background map receives initial grace");
Check(PlacedLightPolicy.Expired(double.NegativeInfinity, 100, 110), "unused map expires at ten seconds");
Check(!PlacedLightPolicy.Expired(109, 100, 110), "tile hit renews expiry");
Check(PlacedLightPolicy.Smooth(0) == 0 && PlacedLightPolicy.Smooth(1) == 1 &&
    PlacedLightPolicy.Smooth(0.5) == 0.5f, "smooth fade endpoints and midpoint");
Check(PlacedLightPolicy.Capacity(774) == 128 && PlacedLightPolicy.Capacity(256) == 41 &&
    PlacedLightPolicy.Capacity(11) == 0, "array capacity reserves six staging layers");
var source = new StaticLightSources.Source(3, 4, 5, 0, 0, 20);
var bake = new PlacedLightBake { Active = true, Slot = 4, Generation = 7, Revision = 2, Source = source };
Check(bake.Budget == 2 && !bake.Complete, "visible bake has two-face budget and starts unpublished");
bake.Advance(2); bake.Advance(2);
Check(!bake.Complete && bake.NextFace == 4, "four-face map is still unpublished");
bake.Advance(2);
Check(bake.Complete && bake.NextFace == 6, "six-face map is publishable");
Check(bake.Matches(4, 7, 2, source with { Hue = 85 }), "HSV change preserves depth identity");
Check(!bake.Matches(4, 8, 2, source) && !bake.Matches(4, 7, 3, source) &&
    !bake.Matches(4, 7, 2, source with { X = 9 }), "reassignment geometry and identity cancel a transaction");
bake = new PlacedLightBake { Active = true, Background = true };
Check(bake.Budget == 1, "background bake has one-face budget");
for (int i = 0; i < 5; i++) bake.Advance(1);
Check(!bake.Complete, "five background faces remain unpublished");
bake.Background = false;
bake.Advance(1);
Check(bake.Complete, "visible upgrade completes remaining background face");
bool rejected = false;
try { new PlacedLightBake { Active = true, Background = true }.Advance(2); }
catch (ArgumentOutOfRangeException) { rejected = true; }
Check(rejected, "background budget cannot be exceeded");

// Identity view: -Z forward. A source center can be outside the screen while
// its sphere still intersects it; near-plane intersections cover every tile.
double[] view = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
float[] projection = { 1,0,0,0, 0,1,0,0, 0,0,-1,-1, 0,0,-1,0 };
var camera = new Vec3d();
PlacedLightView.CameraFromView(view, new double[16], camera);
Check(camera.X == 0 && camera.Y == 0 && camera.Z == 0, "double view camera origin");
view[0] = 0.9999999669;
view[12] = -529000.0 * view[0];
PlacedLightView.CameraFromView(view, new double[16], camera);
Check(Math.Abs(camera.X - 529000.0) < 0.000001,
    "full inverse retains large-world camera position");
view[0] = 1.0;
view[12] = 0.0;
Check(PlacedLightView.Project(12, 0, -10, 4, view, projection).Class == 0,
    "offscreen center with intersecting influence is visible");
Check(PlacedLightView.Priority(PlacedLightView.Project(0, 0, -10, 4, view, projection)) == 0,
    "centered emitter has first admission priority");
Check(PlacedLightView.Priority(PlacedLightView.Project(8, 0, -10, 1, view, projection)) == 1,
    "peripheral emitter retains a visible atlas tier");
Check(PlacedLightView.Priority(PlacedLightView.Project(12, 0, -10, 4, view, projection)) == 2,
    "sphere-only overlap cannot outrank a centered emitter");
Check(PlacedLightView.Project(0, 0, -1, 4, view, projection).Class == 0,
    "camera inside influence stays visible");
Check(PlacedLightView.Project(100, 0, -1, 4, view, projection).Class == 2,
    "near-plane sphere far beyond a side plane is culled");
Check(PlacedLightView.Project(12.4, 0, -10, 1, view, projection).Class == 1,
    "nearby offscreen sphere remains fringe only");
Check(PlacedLightView.Project(100, 0, -10, 4, view, projection).Class == 2,
    "distant offscreen influence is prefetch only");
Check(PlacedLightView.Project(0, 0, 10, 4, view, projection).Class == 2,
    "sphere fully behind camera is excluded");

// Exercise tilting past every viewport side using independently projected
// receiver points inside a light sphere. An emitter may be outside the screen.
int edgeReceivers = 0;
for (int step = -60; step <= 60; ++step) {
    double angle = step * .02, c = Math.Cos(angle), s = Math.Sin(angle);
    double[] tilted = {1,0,0,0, 0,c,s,0, 0,-s,c,0, 0,0,0,1};
    foreach(float radius in new[]{1f,4f,22f}) {
        var bounds = PlacedLightView.Project(3,12,-18,radius,tilted,projection);
        for(int sample=0;sample<64;sample++) {
            double theta=sample*Math.PI*2/64;
            double x=3+radius*.9*Math.Cos(theta),y=12+radius*.9*Math.Sin(theta),z=-18;
            double ey=c*y-s*z,ez=s*y+c*z;
            if(ez>=-.1)continue;
            double nx=x/-ez,ny=ey/-ez;
            if(Math.Abs(nx)>1 || Math.Abs(ny)>1)continue;
            if(!PlacedLightPolicy.Visible(bounds) || nx<bounds.MinX || nx>bounds.MaxX || ny<bounds.MinY || ny>bounds.MaxY)
                throw new Exception("Tilted frustum dropped a visible receiver inside the placed-light sphere");
            ++edgeReceivers;
        }
    }
}
Check(edgeReceivers>1000,"camera tilt preserves all sampled visible sphere receivers");
var fringe = PlacedLightView.Project(11.5,0,-10,1,view,projection);
Check(fringe.Class==1 && fringe.MinX<1 && fringe.MaxX>1 && PlacedLightPolicy.Visible(fringe),
    "residency fringe retains conservative edge bounds and publication");
Check(!PlacedLightPolicy.Visible(PlacedLightView.Project(105,0,-100,1,view,projection)),
    "fully offscreen fringe does not publish an edge tile");

Check(PlacedLightPolicy.Priority(PlacedLightView.Project(0, 0, -100, 4, view, projection), 10000) == 0,
    "centered far emitter precedes nearby peripheral emitter");
Check(PlacedLightPolicy.Priority(PlacedLightView.Project(8, 0, -10, 1, view, projection), 164) == 1,
    "near peripheral emitter has second priority");
Check(PlacedLightPolicy.Priority(PlacedLightView.Project(80, 0, -100, 4, view, projection), 16400) == 2,
    "visible far peripheral emitter has third priority");
Check(PlacedLightPolicy.Priority(PlacedLightView.Project(0, 0, 100, 4, view, projection), 10000) == 3,
    "offscreen emitter is background only");

PlacementProbe.Run(Check, view, projection);
ViewCoverageProbe.Run();
SurfaceCalibrationProbe.Run();

// Compare the new prepared chroma against the public engine adapter over every
// native hue/saturation, avoiding a shader-derived reference implementation.
for (int hue = 0; hue < 64; ++hue) for (int saturation = 0; saturation <= 8; ++saturation)
{
    // Native parity is measured at unit saturation, independently of the user's
    // configured 1.25 saturation default used by the convenience overload.
    StaticLightGpuRecord.Rgb(hue, saturation, 1f, out float r, out float g, out float b);
    int packedChroma = ColorUtil.HsvToRgba(hue * 4, Math.Min(255, saturation * 32), 255);
    Vec3f nativeChroma = new(); ColorUtil.ToRGBVec3f(packedChroma, ref nativeChroma);
    if (Math.Abs(r - nativeChroma.Z) > 1e-7 || Math.Abs(g - nativeChroma.Y) > 1e-7 || Math.Abs(b - nativeChroma.X) > 1e-7)
        throw new Exception($"Prepared native chroma mismatch at {hue}/{saturation}");
}
Console.WriteLine("PASS all 576 prepared native HSV colors match the public engine adapter");
StaticLightGpuRecord.Rgb(13, 4, out float defaultR, out float defaultG, out float defaultB);
StaticLightGpuRecord.Rgb(13, 4, AgxGuiDialog.DefaultPlsSaturation, out float configuredR, out float configuredG, out float configuredB);
Check(defaultR == configuredR && defaultG == configuredG && defaultB == configuredB,
    "prepared chroma convenience overload follows configured saturation default");
