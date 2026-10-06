// DRTAgX deferred include: Directional depth filtering and foliage contact shadows; retains SHADOWQUALITY guards.

#if SHADOWQUALITY > 0
uniform mat4  toShadowMapSpaceMatrixFar;
uniform float shadowRangeFar;
#endif
#if SHADOWQUALITY > 1
uniform mat4  toShadowMapSpaceMatrixNear;
uniform float shadowRangeNear;
#endif

#if SHADOWQUALITY > 0

float df_sunShadow = 1.0;
vec3 drtSunGridReceiverOffset = vec3(0.0);
vec3 drtSunReceiverPlaneNormal = vec3(0.0);

float drtFoliageContact(vec3 receiverView, vec3 receiverNormalView)
{
    // The receiver normal and depth are in view space. Rotate the current
    // directional light there so each foliage hit can project onto that plane.
    if (dot(lightPosition, lightPosition) < 0.0001) return 0.0;
    vec3 lightView = normalize(transpose(mat3(invModelViewMatrix)) * lightPosition);
    float lightFacing = dot(receiverNormalView, lightView);
    if (lightFacing <= 0.05) return 0.0;
    vec3 rayStart = receiverView + lightView * 0.06;
    vec3 rayEnd = receiverView + lightView * 0.65;
    vec4 startClip = drtProjectionMatrix * vec4(rayStart, 1.0);
    vec4 endClip = drtProjectionMatrix * vec4(rayEnd, 1.0);
    if (startClip.w <= 0.0 || endClip.w <= 0.0) return 0.0;

    vec2 startUv = startClip.xy / startClip.w * 0.5 + 0.5;
    vec2 endUv = endClip.xy / endClip.w * 0.5 + 0.5;
    ivec2 size = textureSize(gDepth, 0);

    // These four points only find visible foliage candidates. The caster's
    // reconstructed position, rather than the sample index, places its shadow.
    float contact = 0.0;
    for (int i = 0; i < 4; i++) {
        float t = (float(i) + 0.0) / 24.0;
        vec2 uv = mix(startUv, endUv, t);
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThanEqual(uv, vec2(1.0)))) break;
        ivec2 pixel = ivec2(uv * vec2(size));
        vec4 glowAtHit = texelFetch(gGlow, pixel, 0);
        // Deferred no-cull contact casters use -6..-11 in glow.g, including
        // static cards. Keep their existing contact behavior independent of
        // placed-light classification; forward foliage uses +128 in glow.b.
        if (glowAtHit.g > -5.5 && glowAtHit.b < 128.0) continue;

        float depth = texelFetch(gDepth, pixel, 0).r;
        if (depth >= 1.0) continue;
        vec2 pixelUv = (vec2(pixel) + 0.5) / vec2(size);
        vec4 hit = invProjectionMatrix * vec4(pixelUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
        vec3 hitView = hit.xyz / hit.w;
        // Project the caster back along the light onto the receiving surface's
        // tangent plane. This removes the four face-aligned shadow stamps.
        float castDistance = dot(hitView - receiverView, receiverNormalView) / lightFacing;
        if (castDistance <= 0.02 || castDistance >= 0.65) continue;
        vec3 projected = hitView - lightView * castDistance;
        float proximity = 1.0 - smoothstep(0.02, 0.16,
            distance(projected, receiverView));
        float edge = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
        contact = max(contact, proximity * (1.0 - smoothstep(0.3, 0.65, castDistance)) *
            smoothstep(1.0, 4.0, edge * float(min(size.x, size.y))));
    }
    return contact;
}

float deferredShadowBrightness(vec4 worldPos, float blockBright, float receiverBiasScale)
{
    float len = length(worldPos.xyz);
    float nearSub = 0.0;

#if SHADOWQUALITY > 1
    vec4 scNear = toShadowMapSpaceMatrixNear * worldPos;
    float distanceNear = clamp(
        max(max(0.0, 0.03 - scNear.x) * 100.0, max(0.0, scNear.x - 0.97) * 100.0) +
        max(max(0.0, 0.03 - scNear.y) * 100.0, max(0.0, scNear.y - 0.97) * 100.0) +
        max(0.0, scNear.z - 0.98) * 100.0 +
        max(0.0, len / shadowRangeNear - 0.15), 0.0, 1.0);
    nearSub = scNear.w = clamp(1.0 - distanceNear, 0.0, 1.0);
    if (scNear.z >= 0.999) scNear.w = 0.0;
#endif

    vec4 scFar = toShadowMapSpaceMatrixFar * worldPos;
    float distanceFar = clamp(
        max(max(0.0, 0.03 - scFar.x) * 10.0, max(0.0, scFar.x - 0.97) * 10.0) +
        max(max(0.0, 0.03 - scFar.y) * 10.0, max(0.0, scFar.y - 0.97) * 10.0) +
        max(0.0, scFar.z - 0.98) * 10.0 +
        max(0.0, len / shadowRangeFar - 0.15), 0.0, 1.0);
    distanceFar = distanceFar * 2.0 - 0.5;
    scFar.w = max(0.0, clamp(1.0 - distanceFar, 0.0, 1.0) - nearSub);
    if (scFar.z >= 0.999) scFar.w = 0.0;

    // Match forward PCF and evaluate plane derivatives before cascade branches.
    vec2 farGradient = drtSunFoliageReceiver ?
        drtSunFoliagePlaneGradient(toShadowMapSpaceMatrixFar, shadowMapFar, drtSunReceiverPlaneNormal) :
        dot(drtSunReceiverPlaneNormal, drtSunReceiverPlaneNormal) > 0.0001 ?
        drtSunPlaneGradient(toShadowMapSpaceMatrixFar, drtSunReceiverPlaneNormal) : drtSunReceiverGradient(scFar.xyz);
#if SHADOWQUALITY > 1
    vec2 nearGradient = drtSunFoliageReceiver ?
        drtSunFoliagePlaneGradient(toShadowMapSpaceMatrixNear, shadowMapNear, drtSunReceiverPlaneNormal) :
        dot(drtSunReceiverPlaneNormal, drtSunReceiverPlaneNormal) > 0.0001 ?
        drtSunPlaneGradient(toShadowMapSpaceMatrixNear, drtSunReceiverPlaneNormal) : drtSunReceiverGradient(scNear.xyz);
#endif
    // Keep cascade coverage and plane derivatives continuous. Quantize only
    // the comparison position, without shifting it off its receiving surface.
    scFar.xyz += (toShadowMapSpaceMatrixFar * vec4(drtSunGridReceiverOffset, 0.0)).xyz;
#if SHADOWQUALITY > 1
    scNear.xyz += (toShadowMapSpaceMatrixNear * vec4(drtSunGridReceiverOffset, 0.0)).xyz;
#endif
    float totalFar = 0.0;
    if (scFar.w > 0.0) {
        totalFar = 1.0 - drtSunShadowVisibility(shadowMapFar, scFar.xyz,
            farGradient, 0.00003, receiverBiasScale);
    }
    float b = 1.0 - shadowIntensity * totalFar * scFar.w * DRT_SUN_SHADOW_GAIN;

#if SHADOWQUALITY > 1
    float totalNear = 0.0;
    if (scNear.w > 0.0) {
        totalNear = 1.0 - drtSunShadowVisibility(shadowMapNear, scNear.xyz,
            nearGradient, 0.00002, receiverBiasScale);
    }
    b -= shadowIntensity * totalNear * scNear.w * DRT_SUN_SHADOW_GAIN;
#endif

    df_sunShadow = clamp(b, 0.0, 1.0);
    return df_sunShadow;
}

// Keep foliage contact occlusion confined to the directional term.
float drtDeferredDirectionalBrightness(vec4 worldPos, float blockBright, float grassTag,
    float receiverTag, float sunFactor, vec3 receiverView, vec3 normalWorld, vec3 normalView)
{
    // Only grid snapping follows the toggle; analytic plane filtering stays active.
    drtSunGridReceiverOffset = vec3(0.0);
    if (drtSunGridFrame.w > 0.5)
        drtSunGridReceiverOffset = drtSunGridOffset(
            worldPos.xyz - invModelViewMatrix[3].xyz, normalWorld, drtSunGridCameraPhase);
    drtSunReceiverPlaneNormal = normalWorld;
    float b = deferredShadowBrightness(worldPos, blockBright, mix(1.0, 4.0, grassTag));
    // Only the directional term receives foliage contact occlusion. Preserve
    // the native map when it is already darker, and never dim block light.
    float contactDistance = length(receiverView);
    if (drtContactShadowsEnabled != 0 && receiverTag > 0.5 &&
        shadowIntensity > 0.01 && sunFactor > 0.15 &&
        contactDistance < 12.0 &&
        dot(normalWorld, lightPosition) > 0.05 &&
        df_sunShadow > 1.0 - shadowIntensity * DRT_SUN_SHADOW_GAIN * 0.98) {
        float contact = drtFoliageContact(receiverView, normalize(normalView)) *
            (1.0 - smoothstep(9.0, 12.0, contactDistance));
        df_sunShadow = min(df_sunShadow,
            1.0 - shadowIntensity * DRT_SUN_SHADOW_GAIN * contact);
        b = df_sunShadow;
    }

    return b;
}
#endif
