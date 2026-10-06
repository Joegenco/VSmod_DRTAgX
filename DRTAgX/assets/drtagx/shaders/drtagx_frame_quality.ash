// Runtime quality is uploaded by the native shader Use bridge. Retain the declaration
// even in variants without a quality-dependent effect so caller dictionaries remain valid.
#ifndef DRT_FRAME_QUALITY_UNIFORM
#define DRT_FRAME_QUALITY_UNIFORM
uniform int drtPerformanceMode = 0;
#endif
