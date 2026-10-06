// ============================================================================
// DRTAgX / Vintage Story: AgX Display Transform & Color Grading Pipeline
// ----------------------------------------------------------------------------
// Mathematical Basis:
//   1. Scene-linear exposure scaling: exp2(Exposure + autoEv - 1.0)
//   2. Engine gamma/contrast pre-grading with power functions and sepia rotation.
//   3. AgX Log2 Encoding: defaults to [-9.5 EV, +3.2 EV] mapped to [0, 1].
//   4. AgX Inset Color Transform: compresses primaries toward white point to prevent gamut clipping.
//   5. 6th-order Horner Polynomial: smooth sigmoid tone curve approximating AgX filmic look.
//   6. Output Display Gamma: 2.2 + contrastLevel.
// ============================================================================

// Match the configuration/reset defaults when a program has not received uniforms yet.
uniform float AgxMinEv = -9.5;
uniform float AgxMaxEv = 3.2;
uniform float GreyPoint = 0.18;
uniform float INV_GREY_POINT = 5.5555556;
uniform float Exposure = 0.0;
uniform sampler2D drtExposureTex;
uniform int drtExposureEnabled = 0;

const mat3 AgXInsetMatrix = mat3(
    0.842479, 0.042328, 0.042376,
    0.078434, 0.878469, 0.078434,
    0.079224, 0.079166, 0.879143
);

const mat3 AgXInsetMatrixSquared = mat3(
    0.716448, 0.076199, 0.076276,
    0.141193, 0.781237, 0.141179,
    0.142603, 0.142497, 0.782459
);

// Exposure and display transform remain in their original order.
vec3 ApplyAgX(vec3 color) {

    // Preserve the existing minimum input before exposure and logarithmic encoding.
    color.rgb = max(color.rgb, 0.0);

    color += (contrastLevel * 0.015);
 
    color = AgXInsetMatrix * color;
    color = clamp(log2(max(color, vec3(0.00005))), vec3(AgxMinEv), vec3(AgxMaxEv));
    
    color = (color - vec3(AgxMinEv)) / (AgxMaxEv - AgxMinEv);
	color += 0.055;
    color = clamp(color, 0.0003, 1.0);
    
    // Evaluate the fitted AgX contrast curve in Horner form.
    color = (((((15.41 * color - 40.22) * color + 32.1) * color - 6.868) * color + 0.29) * color + 0.286) * color - 0.001;

    color = pow(max(color, vec3(0.0)), vec3(2.2 + contrastLevel));

    // Emit the display transform directly; dark radiance gets no pixel noise.
    return color;
}

vec4 ColorGrade(vec4 color) {
    // Exposure scales scene-linear HDR, including bloom and final-stage light effects.
    float autoEv = drtExposureEnabled != 0 ? texture(drtExposureTex, vec2(0.5)).r : 0.0;
    color.rgb *= exp2(Exposure + autoEv - 1);
    // Apply the engine grading controls before the AgX display transform.
    vec3 combinedGamma = vec3(2.4 / (max(gammaLevel, 0.001) * max(extraGamma, 0.001)));
    
    // Negative radiance is invalid and would make the fractional power undefined.
    color.rgb = pow(max(color.rgb, vec3(0.0)), combinedGamma);
    color.rgb *= brightnessLevel;

    vec3 sepiaMultiplier = vec3(1.0 + sepiaLevel * 0.1, 1.0, 1.0 - sepiaLevel * 0.1);
    color.rgb *= sepiaMultiplier;
                        
    vec3 sepcol = AgXInsetMatrixSquared * color.rgb;
    
    color.rgb = mix(color.rgb, sepcol, sepiaLevel);
    
    vec3 contrastPow = vec3(1.25 + contrastLevel * 0.1666667); // / 6.0
    color.rgb = pow(max(color.rgb * INV_GREY_POINT, vec3(0.0001)), contrastPow);
    
    float contrastMult = 0.75 + contrastLevel * 0.2;
    color.rgb *= GreyPoint * contrastMult;
    
    if (glitchEffectStrength > 0.0) {
        float g = gnoise(vec3(texCoord.xy * 2000.0, mod(windWaveCounter * 30.0, 100.0)));
        color.rgb *= mix(1.0, clamp(0.7 + g * 0.5, 0.7, 1.0), glitchEffectStrength);
        
        vec3 glitchMultiplier = vec3(
            1.0 + glitchEffectStrength * 0.75, 
            1.0 + glitchEffectStrength * 0.1, 
            1.0 - glitchEffectStrength * 0.2
        );
        color.rgb *= glitchMultiplier;
        color.a += glitchEffectStrength * 0.3333333;
    }
    
    color.rgb = ApplyAgX(color.rgb);
    
    return color;   
}

