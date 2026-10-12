// Native Decor replay shares the terrain's completed diffuse illumination.
// Its intentional clip-W/ZOffset does not enter this coplanarity test.
uniform int drtDeferredDecalLighting = 0;
uniform isampler2D drtReceiverPlane;
uniform sampler2D drtDeferredDecalIrradiance;
bool drtReadDecalIrradiance(out vec3 irradiance) {
    irradiance=vec3(0.0);
    if(drtDeferredDecalLighting==0 || drtFragmentReceiverPlane==DRT_RECEIVER_INVALID_PLANE ||
       drtFragmentReceiverToken==0u)return false;
    if(drtReceiverUnbiasedClip.w<=0.0)return false;
    vec2 uv=drtReceiverUnbiasedClip.xy/drtReceiverUnbiasedClip.w*0.5+0.5;
    if(any(lessThan(uv,vec2(0))) || any(greaterThanEqual(uv,vec2(1))))return false;
    // This is the material's original geometric projection, before native
    // clip-W depth separation. Preserve the native biased raster/depth test.
    ivec2 pixel=ivec2(floor(uv*vec2(textureSize(drtDeferredDecalIrradiance,0))));
    if(texelFetch(drtReceiverPlane,pixel,0).r!=drtFragmentReceiverPlane)return false;
    vec4 light=texelFetch(drtDeferredDecalIrradiance,pixel,0);
    float normalPayload=light.a;
    if(normalPayload>-1.0 || normalPayload<=-16.0 ||
       drtPlacedSurfaceNormalToken(normalPayload)!=drtFragmentReceiverToken)return false;
    irradiance=light.rgb;return true;
}
