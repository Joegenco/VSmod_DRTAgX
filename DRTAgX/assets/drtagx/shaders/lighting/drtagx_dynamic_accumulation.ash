// Caller supplies DRT_DYNAMIC_CAPACITY and positive surface contributions.
const int DRT_DYNAMIC_BUDGET = DRT_LIGHT_BUDGET;
const float DRT_DYNAMIC_TIE_WIDTH = DRT_LIGHT_TIE_WIDTH;

float drtRelativeStrength(float a, float b) {
    return drtLightRelativeStrength(a, b);
}

vec3 drtAccumulateDynamic(vec3 contributions[DRT_DYNAMIC_CAPACITY], int count) {
    if (count <= 0) return vec3(0.0);
    if (count == 1) {
        float s = dot(contributions[0], DRT_LUMINANCE);
        return s > 0.0 && !isnan(s) && !isinf(s) ? contributions[0] : vec3(0.0);
    }
    DrtLightRanks ranks;
    
    drtClearRanks(ranks);
    for (int i = 0; i < min(count, DRT_DYNAMIC_CAPACITY); ++i)
        drtInsertRank(ranks, contributions[i]);
    
    return drtRankRadiance(ranks);
}
