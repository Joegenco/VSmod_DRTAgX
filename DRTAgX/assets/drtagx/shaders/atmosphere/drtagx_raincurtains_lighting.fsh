// RainCurtains passes the same Ambient.BlendedFogColor captured in DrtAtmosphere.
// Its shownRel varying is the world-space camera ray after cloud curvature.
vec3 drtRainCurtainFog(vec3 nativeFog, vec3 shownRel) {
    float lengthSquared = dot(shownRel, shownRel);
    // Preserve native startup and underwater tint: dry sky radiance is not a
    // substitute for the provider's submerged color/depth handling.
    if (drtFogColor.w < 0.5 || drtAtmosphereView.z > 0.7 || lengthSquared < 1e-8)
        return nativeFog;
    // Change the fog-color carrier before the provider's existing cloud/fog
    // blend. cloudTint already carries drtCloudLight and additive lightning;
    // multiplying the final tint would apply environmental lighting twice.
    return drtWeatherRadiance(shownRel * inversesqrt(lengthSquared));
}
