// Native fog/light setter keys are shared by geometry and cloud-map programs.
// Keep uniforms separate from geometry varyings so utility-only passes can use
// the same engine contract without requiring nonexistent vertex attributes.
uniform float flatFogDensity;
uniform float flatFogStart;
uniform float viewDistance;
uniform float viewDistanceLod0;
uniform float zNear;
uniform float zFar;
uniform vec3 lightPosition;
uniform float shadowIntensity;

#if SHADOWQUALITY > 0
uniform sampler2DShadow shadowMapFar;
uniform float shadowMapWidthInv;
uniform float shadowMapHeightInv;
#endif
#if SHADOWQUALITY > 1
uniform sampler2DShadow shadowMapNear;
#endif

uniform float windWaveCounter;
uniform float glitchStrength;
uniform float psychedelicStrength;
