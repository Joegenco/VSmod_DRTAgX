// The continuous RGB fog fade keeps native coverage/depth throughout the band.
// Only fully concealed geometry is discarded, releasing clouds/stars at the
// endpoint without any spatial dithering or partially exposed terrain holes.
void drtDiscardTerrainBoundary(vec3 worldPos) {
#ifdef DRT_FOG_TRANSPORT
    if (drtFogColor.w < 0.5 || drtAtmosphereView.x <= 0.0) return;
    float distance = length((worldPos - drtAtmosphereCamera.xyz).xz);
    if (distance >= drtBoundaryEnd()) discard;
#endif
}
