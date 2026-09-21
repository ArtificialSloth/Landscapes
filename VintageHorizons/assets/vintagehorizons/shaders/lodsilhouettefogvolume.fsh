#version 330 core

// A single depth-aware integration through a smooth world-space field. Unlike crossing cards,
// this has no internal surfaces whose alpha can stack into lines at corners or height slices.

uniform sampler2D sceneDepth;
uniform sampler2D fogField;
uniform mat4 invProjection;
uniform mat4 invView;
uniform vec3 cameraInField;
uniform float fieldWorldOriginX;
uniform float fieldWorldOriginZ;
uniform float fieldWidth;
uniform float fieldHeight;
uniform float fieldTexelBlocks;
uniform float fieldMinY;
uniform float fieldMaxY;
uniform float farViewDistance;
uniform float globeStart;
uniform float globeEffect;

uniform vec4 rgbaFogIn;
uniform float fogStrength;
uniform float fogCoverage;
uniform float fogSpillBlocks;
uniform float fogHeightBlocks;
uniform float fogFloorDepthBlocks;
uniform float fogBillowSizeBlocks;
uniform float fogEdgeSoftness;
uniform float fogLayerDetail;
// Distance the billows have already drifted, in blocks, integrated on the CPU.
uniform float fogDriftBlocks;

layout(location = 0) out vec4 outColor;

const float GuardProfile = -0.55;
const float FogExtinctionPerBlock = 0.018;
const float SeamSealIntegralFraction = 0.54;
const int MaxFogSteps = 160;

float vhFogHash(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float vhFogNoise(vec3 p)
{
    vec3 cell = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = vhFogHash(cell + vec3(0.0, 0.0, 0.0));
    float n100 = vhFogHash(cell + vec3(1.0, 0.0, 0.0));
    float n010 = vhFogHash(cell + vec3(0.0, 1.0, 0.0));
    float n110 = vhFogHash(cell + vec3(1.0, 1.0, 0.0));
    float n001 = vhFogHash(cell + vec3(0.0, 0.0, 1.0));
    float n101 = vhFogHash(cell + vec3(1.0, 0.0, 1.0));
    float n011 = vhFogHash(cell + vec3(0.0, 1.0, 1.0));
    float n111 = vhFogHash(cell + vec3(1.0, 1.0, 1.0));
    float n00 = mix(n000, n100, f.x);
    float n10 = mix(n010, n110, f.x);
    float n01 = mix(n001, n101, f.x);
    float n11 = mix(n011, n111, f.x);
    return mix(mix(n00, n10, f.y), mix(n01, n11, f.y), f.z);
}

float vhFogFbm(vec3 p)
{
    float value = 0.0;
    value += 0.57 * vhFogNoise(p);
    p = p * 2.03 + vec3(11.7, 5.3, 17.1);
    value += 0.29 * vhFogNoise(p);
    p = p * 2.01 + vec3(3.1, 19.4, 7.8);
    value += 0.14 * vhFogNoise(p);
    return value;
}

bool vhFogSlab(float origin, float direction, float minimum, float maximum,
    inout float nearDistance, inout float farDistance)
{
    if (abs(direction) < 0.000001)
        return origin >= minimum && origin <= maximum;
    float first = (minimum - origin) / direction;
    float second = (maximum - origin) / direction;
    if (first > second)
    {
        float swap = first;
        first = second;
        second = swap;
    }
    nearDistance = max(nearDistance, first);
    farDistance = min(farDistance, second);
    return nearDistance <= farDistance;
}

float vhFogCrossingProfile(float signedDistance)
{
    float lodPlateau = -min(14.0, fogSpillBlocks * 0.29);
    float farseerPlateau = min(32.0, fogSpillBlocks * 0.67);
    float farseerReach = fogSpillBlocks + min(32.0, fogSpillBlocks * 0.67);
    float linearProfile;
    if (signedDistance < lodPlateau)
        linearProfile = (signedDistance + fogSpillBlocks) / (lodPlateau + fogSpillBlocks);
    else if (signedDistance <= farseerPlateau)
        linearProfile = 1.0;
    else
        linearProfile = (farseerReach - signedDistance)
            / (farseerReach - farseerPlateau);
    return clamp(linearProfile, 0.0, 1.0);
}

float vhFogSeamSealDensity(float signedDistance, float endFade)
{
    // A noisy volumetric bank still needs to fulfill its actual job: the exact terrain handoff
    // must not show through a low-density pocket. This is a broad normalized extinction core,
    // not another surface. Its one-sided integral from LOD terrain to the seam is a stable target
    // optical depth at any spill width; the Farseer side is deliberately allowed to be wider.
    float lodReach = clamp(fogSpillBlocks * 0.67, 12.0, 32.0);
    float sideReach = signedDistance < 0.0
        ? lodReach : clamp(fogSpillBlocks, 12.0, 48.0);
    float across = abs(signedDistance) / sideReach;
    float profile = 1.0 - smoothstep(0.08, 1.0, across);
    float targetOpticalDepth = mix(3.0, 4.5, clamp(fogCoverage, 0.0, 1.0));
    float normalization = FogExtinctionPerBlock * SeamSealIntegralFraction * lodReach;
    return profile * clamp(endFade, 0.0, 1.0)
        * targetOpticalDepth / max(0.0001, normalization);
}

void main()
{
    ivec2 depthSize = textureSize(sceneDepth, 0);
    ivec2 depthTexel = clamp(ivec2(gl_FragCoord.xy), ivec2(0), depthSize - ivec2(1));
    vec2 screenUv = (vec2(depthTexel) + 0.5) / vec2(depthSize);
    float opaqueDepth = texelFetch(sceneDepth, depthTexel, 0).r;
    // Pull an exact camera-relative endpoint from opaque depth. The tiny sky clamp avoids an
    // infinite-view projection producing w=0; the march is capped to Farseer's reach below.
    float reconstructDepth = min(opaqueDepth, 0.999999);
    vec4 viewPoint = invProjection
        * vec4(screenUv * 2.0 - 1.0, reconstructDepth * 2.0 - 1.0, 1.0);
    if (abs(viewPoint.w) < 0.0000001) discard;
    viewPoint /= viewPoint.w;
    vec3 relativeEndpoint = (invView * viewPoint).xyz;
    float endpointDistance = min(length(relativeEndpoint), farViewDistance);
    if (endpointDistance <= 0.001) discard;
    vec3 rayDirection = normalize(relativeEndpoint);

    float fieldBlocksX = fieldWidth * fieldTexelBlocks;
    float fieldBlocksZ = fieldHeight * fieldTexelBlocks;
    float marchStart = 0.0;
    float marchEnd = endpointDistance;
    if (!vhFogSlab(cameraInField.x, rayDirection.x, 0.0, fieldBlocksX,
            marchStart, marchEnd)
        || !vhFogSlab(cameraInField.y, rayDirection.y, fieldMinY, fieldMaxY,
            marchStart, marchEnd)
        || !vhFogSlab(cameraInField.z, rayDirection.z, 0.0, fieldBlocksZ,
            marchStart, marchEnd)
        || marchEnd <= max(0.0, marchStart)) discard;

    float baseStep = mix(7.5, 3.0, clamp(fogLayerDetail, 0.0, 1.0));
    // Spatial dithering turns the residual fixed-step error into fine grain instead of coherent
    // view-facing bands. The field and density remain world anchored.
    float rayDistance = max(0.0, marchStart)
        + baseStep * vhFogHash(vec3(gl_FragCoord.xy, 0.731));
    float transmittance = 1.0;
    vec3 scattered = vec3(0.0);
    float broadScale = 1.0 / max(1.0, fogBillowSizeBlocks);
    vec3 drift = vec3(fogDriftBlocks, 0.0, fogDriftBlocks * -0.67);
    float farseerReach = fogSpillBlocks + min(32.0, fogSpillBlocks * 0.67);
    float maxWobble = min(18.0, fogSpillBlocks * 0.35);
    float conservativeReach = farseerReach + maxWobble + fieldTexelBlocks * 1.5;

    for (int stepIndex = 0; stepIndex < MaxFogSteps; stepIndex++)
    {
        if (rayDistance >= marchEnd || transmittance < 0.015) break;
        vec3 relative = rayDirection * rayDistance;
        vec3 fieldPosition = cameraInField + relative;
        vec2 fieldUv = fieldPosition.xz / vec2(fieldBlocksX, fieldBlocksZ);
        if (any(lessThan(fieldUv, vec2(0.0))) || any(greaterThan(fieldUv, vec2(1.0))))
            break;

        vec4 field = texture(fogField, fieldUv);
        float horizontalOutside = max(0.0, field.b - conservativeReach);
        float horizontal = length(relative.xz);
        float globe = clamp((horizontal - globeStart)
            / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
        float globeDrop = globeEffect * globe * globe * farViewDistance;
        float groundY = field.g - globeDrop;
        float heightAboveGround = fieldPosition.y - groundY;
        float verticalOutside = max(0.0, max(
            -fogFloorDepthBlocks - heightAboveGround,
            heightAboveGround - fogHeightBlocks));
        // A ray that follows the seam while above or below its local terrain must still advance;
        // otherwise it can spend the whole fixed budget walking empty air in tiny steps.
        float outside = max(horizontalOutside, verticalOutside * 0.35);
        float stepBlocks = outside > 0.0
            ? clamp(outside * 0.72, baseStep, 512.0) : baseStep;
        stepBlocks = min(stepBlocks, marchEnd - rayDistance);

        if (horizontalOutside <= 0.0 && verticalOutside <= 0.0)
        {
            float lowerFade = max(2.0, min(10.0, fogFloorDepthBlocks * 0.35 + 2.0));
            float vertical = smoothstep(-fogFloorDepthBlocks,
                    -fogFloorDepthBlocks + lowerFade, heightAboveGround)
                * (1.0 - smoothstep(fogHeightBlocks * 0.55,
                    fogHeightBlocks, heightAboveGround));

            // Noise is evaluated in the unwarped world so its scale and drift do not stretch as
            // the distant globe curve lowers geometry.
            vec3 fogWorldPosition = vec3(
                fieldWorldOriginX + fieldPosition.x,
                fieldPosition.y + globeDrop,
                fieldWorldOriginZ + fieldPosition.z);
            vec3 broadPoint = (fogWorldPosition + drift)
                * vec3(broadScale, broadScale * 1.40, broadScale);
            float broad = vhFogFbm(broadPoint);
            float detail = vhFogNoise((fogWorldPosition + drift * 1.35)
                * vec3(broadScale * 2.75, broadScale * 3.60, broadScale * 2.75));
            float billow = smoothstep(0.28, 0.80, broad * 0.74 + detail * 0.26);

            vec3 wobblePoint = (fogWorldPosition + drift * 0.8) * broadScale;
            float wobble = (vhFogNoise(wobblePoint * 1.10) - 0.5) * 0.80
                + (vhFogNoise(wobblePoint * 2.60 + vec3(37.1, 11.9, 53.7)) - 0.5)
                    * 0.80;
            float signedDistance = field.r + wobble * fogEdgeSoftness * maxWobble;
            float crossing = vhFogCrossingProfile(signedDistance);
            // The open-end value fades toward a negative guard. At zero it cannot reappear even
            // when edge wobble is at its maximum.
            crossing = GuardProfile + (crossing - GuardProfile) * clamp(field.a, 0.0, 1.0);
            crossing = clamp(crossing, 0.0, 1.0);
            crossing = crossing * crossing * (3.0 - 2.0 * crossing);

            float noiseDensity = mix(0.10 + 0.50 * clamp(fogCoverage, 0.0, 1.0),
                1.45, billow);
            // The organic field shapes the bank while a stable core seals the exact ownership
            // transition. Using the unwobbled distance for the core prevents noise from opening
            // a crack; both terms are integrated together and remain free of internal surfaces.
            float seamSealDensity = vhFogSeamSealDensity(field.r, field.a);
            float density = vertical * max(0.0, fogStrength)
                * (crossing * noiseDensity + seamSealDensity);
            float segmentAlpha = 1.0 - exp(
                -FogExtinctionPerBlock * density * stepBlocks);
            float contribution = transmittance * segmentAlpha;
            vec3 colour = rgbaFogIn.rgb * mix(0.92, 1.035, broad);
            scattered += colour * contribution;
            transmittance *= 1.0 - segmentAlpha;
        }
        rayDistance += max(0.25, stepBlocks);
    }

    float alpha = clamp(1.0 - transmittance, 0.0, 0.985);
    if (alpha < 0.002) discard;
    outColor = vec4(scattered / max(alpha, 0.0001), alpha);
}
