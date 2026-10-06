// DRTAgX deferred include: Shared six-face selection for point-light depth maps.

int drtCubeFace(vec3 direction)
{
    vec3 a = abs(direction);
    if (a.x >= a.y && a.x >= a.z) return direction.x > 0.0 ? 0 : 1;
    if (a.y >= a.z) return direction.y > 0.0 ? 2 : 3;
    return direction.z > 0.0 ? 4 : 5;
}
