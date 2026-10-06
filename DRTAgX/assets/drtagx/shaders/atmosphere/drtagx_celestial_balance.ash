// Apply after sprite phase shading, and to LUT source strength before scattering.
// A shared linear gain keeps moon surface, atmospheric halo and cloud light consistent.
#ifndef DRT_CELESTIAL_BALANCE
#define DRT_CELESTIAL_BALANCE
const float DRT_MOON_GAIN = 0.5;
// Visible sprites have independent artistic gains. Changing these does not
// amplify the atmospheric moon halo or the ambient/cloud lighting carriers.
// Unit-white sun texels contribute 16.0 HDR before atmospheric attenuation and AgX.
// The native sun consumes its gain in standard's scoped sprite path; the native
// moon consumes its gain in celestialobject. Changing the unused sun branch alone
// in celestialobject cannot change the engine's actual sun draw.
const float DRT_SUN_SPRITE_GAIN = 4.0;
const float DRT_MOON_SPRITE_GAIN = 4.0;

float drtMoonDayGain(float sunElevation) {
    // Two stops = one quarter of intrinsic sprite radiance in daylight.
    // Solar elevation gives a smooth horizon-to-10-degree transition; native
    // dayLight can lag the sun, and nighttime moon/ambient light stays intact.
    return mix(1.0, 0.25, smoothstep(0.0, sin(radians(10.0)), sunElevation));
}
#endif
