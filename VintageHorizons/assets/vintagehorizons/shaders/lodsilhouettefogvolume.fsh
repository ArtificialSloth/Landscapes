#version 330 core

// A single depth-aware integration through a smooth world-space field. Unlike crossing cards,
// this has no internal surfaces whose alpha can stack into lines at corners or height slices.
//
// It runs at half resolution (0.4.124): each pixel marches the ray of one texel of its 2x2
// block, alternately the farthest and the nearest, and records that ray's length so
// lodsilhouettefogupsample.fsh can rebuild full resolution without spreading fog across depth
// edges. Output is premultiplied: fog colour times opacity, and opacity.
//
// The medium is shaped like a real fog bank (0.4.123). Its vertical column over the measured
// seam band, widened by a margin and a gently fading seal so slanted views are covered too, is
// held just opaque rather than many times over. Beyond that band the bank thins exponentially, noise
// erodes its edges into strands instead of dimming the whole pattern, a faint haze carries the
// fade on, and sunlight shades it: bright tops, grey-blue undersides, never brighter than the
// horizon. Mirrored for the checks by LodSilhouetteFogVolume.

uniform sampler2D sceneDepth;
uniform sampler2D fogField;
// Tiling value noise, 16 lattice cells per repeat, four independent channels
// (LodSilhouetteFogNoise). The engine's uniform collector does not know sampler3D, so the draw
// binds this unit with GL directly.
uniform sampler3D fogNoise;
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
// Blocks one pixel covers per block of distance, so detail smaller than a pixel is filtered.
uniform float fogPixelScale;

uniform vec4 rgbaFogIn;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;
// Outer density: the bank away from the seam, and its haze.
uniform float fogStrength;
// Density over the seam: the sealed core and the bank above it.
uniform float fogSeamStrength;
uniform float fogCoverage;
uniform float fogSpillBlocks;
uniform float fogHeightBlocks;
uniform float fogFloorDepthBlocks;
uniform float fogBillowSizeBlocks;
uniform float fogEdgeSoftness;
uniform float fogLayerDetail;
// Half-width of the band that must stay hidden, measured from how far the real 32-block
// staircase strays from the rounded centreline.
uniform float fogCoreBlocks;
// Distance the billows have already drifted, in blocks, integrated on the CPU.
uniform float fogDriftBlocks;

layout(location = 0) out vec4 outFog;
layout(location = 1) out float outDistance;

// Optical depths are quoted through one vertical column over the core.
const float SealOpticalDepth = 3.5;
const float BankOpticalDepth = 3.2;
const float HazeOpticalDepth = 0.25;
const float BankMeanDensity = 0.6;
const float ShoulderFraction = 0.4;
const float HazeFraction = 0.6;
const float SealFalloffFraction = 0.3;
const float FarseerReachScale = 1.6;
const float GroundLayerFraction = 0.3;
const float ScaleHeightFraction = 0.42;
const float SealColumnFraction = 0.5923;
const float BankColumnFraction = 0.5548;
const float NoisePeriod = 16.0;
const float NoiseTexelsPerCell = 4.0;
// Past this mip a 64-texel repeat is two texels or fewer: the lookup is the channel's mean.
const float NoiseFlatLod = 5.0;
const float SunProbeBlocks = 14.0;
// Steps through thin fog may grow to this fraction of the distance: far away the fog's detail
// is smaller than a pixel, and a ray skimming along the seam must still reach its far end
// within the step budget. Dense fog keeps short steps whatever the distance.
const float FarStepFraction = 0.02;
const int MaxFogSteps = 192;

float vhFogHash(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
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

// Mip level at which one noise texel covers a pixel, for a lookup scaled by cellsPerBlock.
float vhFogNoiseLod(float pixelBlocks, float cellsPerBlock)
{
    return log2(max(0.0001, pixelBlocks * cellsPerBlock * NoiseTexelsPerCell));
}

vec4 vhFogNoise(vec3 cells, float lod)
{
    return textureLod(fogNoise, cells * (1.0 / NoisePeriod), max(0.0, lod));
}

// Three octaves across three channels, so the octaves are independent patterns. An octave
// whose texels are all smaller than a pixel is its mean, 0.5, so its fetch is skipped.
float vhFogFbm(vec3 q, float lod)
{
    float second = lod + 1.02 < NoiseFlatLod
        ? vhFogNoise(q * 2.03 + vec3(11.7, 5.3, 17.1), lod + 1.02).g : 0.5;
    float third = lod + 2.03 < NoiseFlatLod
        ? vhFogNoise(q * 4.09 + vec3(3.1, 19.4, 7.8), lod + 2.03).b : 0.5;
    return 0.57 * vhFogNoise(q, lod).r + 0.29 * second + 0.14 * third;
}

// The coverage remap's mean over evenly spread noise. Where the billows are smaller than a
// pixel the filtered noise flattens toward its middle value, which would erase the thin fringe
// instead of averaging it; the mean keeps the fringe as the veil it would look like.
float vhFogExpectedDensity(float threshold)
{
    return threshold >= 0.0
        ? max(0.0, 0.5 * (1.0 - threshold))
        : (0.5 - threshold) / (1.0 - threshold);
}

float vhFogPhase(float cosine, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / pow(1.0 + g2 - 2.0 * g * cosine, 1.5);
}

// Extinction (optical depth per block) at one point of the bank. `field` is the fog field at
// that point and `height` its height above the seam ground. `cheap` is the sun probe's
// cut-down form. Each term is normalized by its own column, so its vertical integral is the
// named optical depth whatever the bank's local height.
float vhFogExtinction(vec3 fogWorld, vec4 field, float height, float pixelBlocks, bool cheap,
    out float top)
{
    float billowBlocks = max(1.0, fogBillowSizeBlocks);
    float cellsPerBlock = 1.0 / billowBlocks;
    vec3 world = fogWorld + vec3(fogDriftBlocks, 0.0, fogDriftBlocks * -0.67);
    float signedDistance = field.r;
    float endFade = clamp(field.a, 0.0, 1.0);

    // Broad, vertically flattened lookup: the rim's wander, the swirl, the top's undulation.
    vec3 broadCells = world * vec3(cellsPerBlock * 0.55, cellsPerBlock * 0.15,
        cellsPerBlock * 0.55);
    vec4 broad = vhFogNoise(broadCells, vhFogNoiseLod(pixelBlocks, cellsPerBlock * 0.55));

    // 1. Lateral: nothing thins inside the measured core; beyond it the bank fades
    //    exponentially, further over Farseer. The rim wanders before shaping (G225).
    float beyond = max(0.0, abs(signedDistance) - fogCoreBlocks);
    float wanderBlocks = clamp(fogEdgeSoftness, 0.0, 1.0) * 0.5 * fogSpillBlocks;
    float rim = max(0.0, beyond + (broad.r - 0.5) * 1.8 * wanderBlocks);
    float side = signedDistance < 0.0 ? fogSpillBlocks : fogSpillBlocks * FarseerReachScale;
    // Past one and a half shoulders nothing remains, which is what bounds the march.
    float cut = 1.0 - smoothstep(side, side * 1.5, rim);
    float envelope = exp(-rim / max(1.0, side * ShoulderFraction)) * endFade * cut;

    // 2. Vertical: tallest over the seam and lower toward the rim, with an undulating top; a
    //    dense ground layer, then exponential thinning.
    top = fogHeightBlocks * (0.45 + 0.55 * envelope) * (0.7 + 0.6 * broad.a);
    float hs = max(0.0, height);
    float scaleHeight = max(1.0, ScaleHeightFraction * top);
    float heightDensity = exp(-max(0.0, hs - GroundLayerFraction * top) / scaleHeight);
    float lowerFade = max(2.0, min(10.0, fogFloorDepthBlocks * 0.35 + 2.0));
    float under = smoothstep(-fogFloorDepthBlocks, -fogFloorDepthBlocks + lowerFade, height);
    float ceiling = 1.0 - smoothstep(0.35 * top, 1.1 * top, hs);

    // 3. Coverage erosion, not dimming: near the seam every noise value is fog; toward the rim
    //    only the peaks survive, as separate strands. The swirl and a frame stretched along
    //    the wind and flattened vertically make those strands streaks rather than balls.
    vec3 wind = normalize(vec3(1.0, 0.0, -0.67));
    vec3 crosswind = vec3(-wind.z, 0.0, wind.x);
    vec3 swirled = world + vec3(broad.g - 0.5, 0.0, broad.b - 0.5) * (billowBlocks * 1.1);
    vec3 q = vec3(dot(swirled, wind) * 0.5, swirled.y * 2.2, dot(swirled, crosswind))
        * cellsPerBlock;
    float baseLod = vhFogNoiseLod(pixelBlocks, cellsPerBlock);
    float noise = cheap ? vhFogNoise(q, baseLod).r * 0.9 + 0.05 : vhFogFbm(q, baseLod);
    noise = smoothstep(0.18, 0.82, noise);
    float coverage = envelope * ceiling;
    float threshold = 1.0 - coverage * mix(1.0, 1.7, clamp(fogCoverage, 0.0, 1.0));
    float density = clamp((noise - threshold) / max(0.001, 1.0 - threshold), 0.0, 1.0);
    density = mix(density, vhFogExpectedDensity(threshold), smoothstep(1.0, 3.0, baseLod));

    // 4. Feathery edges: finer thread-like noise frays the thin parts, and fades out where its
    //    threads would be narrower than a pixel and sparkle.
    if (!cheap)
    {
        float detailLod = vhFogNoiseLod(pixelBlocks, cellsPerBlock * 3.3);
        float detailWeight = 1.0 - smoothstep(-0.3, 1.4, detailLod);
        if (detailWeight > 0.0)
        {
            float thread = vhFogNoise(q * vec3(3.3, 2.7, 3.3) + vec3(5.2, 1.7, 9.4),
                detailLod).a;
            float ridge = 1.0 - abs(2.0 * thread - 1.0);
            float erosion = clamp(fogEdgeSoftness, 0.0, 1.0) * 0.8 * ridge * detailWeight
                * (1.0 - 0.7 * density);
            density = clamp((density - erosion) / max(0.001, 1.0 - erosion), 0.0, 1.0);
        }
    }
    // Thin, translucent strands near the rim rather than opaque puffs.
    density *= pow(envelope, 0.75);

    float columnBlocks = max(1.0, top);
    float bank = BankOpticalDepth / (BankColumnFraction * columnBlocks * BankMeanDensity)
        * density * heightDensity;

    // 5. The seal: noise cannot open the measured core. Its column there is exactly
    //    SealOpticalDepth, the same scale as the bank's, so it fills holes instead of
    //    becoming a second, denser wall; it dies away under the bank's shoulder.
    float sealFalloff = max(1.0, SealFalloffFraction * fogSpillBlocks);
    float sealProfile = exp(-beyond / sealFalloff) * endFade;
    // Past the core the seal takes on the bank's billows, so its fade is lobed like the bank's
    // edge instead of a clean offset of the centreline. Inside the core it stays whole.
    float sealBillows = mix(1.0, 0.3 + 0.7 * noise, smoothstep(0.0, sealFalloff, beyond));
    float sealCeiling = 1.0 - smoothstep(0.5 * top, 1.15 * top, hs);
    float seal = SealOpticalDepth / (SealColumnFraction * columnBlocks)
        * sealProfile * sealBillows * heightDensity * sealCeiling;

    // 6. Haze: real fog never ends at a line.
    float haze = HazeOpticalDepth / (BankColumnFraction * columnBlocks)
        * exp(-rim / max(1.0, side * HazeFraction)) * endFade * cut * heightDensity * ceiling;

    // 7. Two densities: over the seam the fog takes the seam density, blending out over the
    //    shoulder to the outer density; the wandering rim keeps that blend lobed, not a band.
    float seamWeight = exp(-rim / max(1.0, side * ShoulderFraction));
    float bankStrength = mix(max(0.0, fogStrength), max(0.0, fogSeamStrength), seamWeight);
    return (max(bank * bankStrength, seal * max(0.0, fogSeamStrength))
        + haze * max(0.0, fogStrength)) * under;
}

void main()
{
    // One texel of this pixel's 2x2 block: the farthest on even checkerboard squares and the
    // nearest on odd ones, so the upsample finds a matching ray on both sides of a depth edge.
    ivec2 depthSize = textureSize(sceneDepth, 0);
    ivec2 lowTexel = ivec2(gl_FragCoord.xy);
    bool wantFar = ((lowTexel.x + lowTexel.y) & 1) == 0;
    ivec2 depthTexel = clamp(lowTexel * 2, ivec2(0), depthSize - ivec2(1));
    float opaqueDepth = texelFetch(sceneDepth, depthTexel, 0).r;
    for (int corner = 1; corner < 4; corner++)
    {
        ivec2 candidate = clamp(lowTexel * 2 + ivec2(corner & 1, corner >> 1),
            ivec2(0), depthSize - ivec2(1));
        float candidateDepth = texelFetch(sceneDepth, candidate, 0).r;
        if (wantFar ? candidateDepth > opaqueDepth : candidateDepth < opaqueDepth)
        {
            depthTexel = candidate;
            opaqueDepth = candidateDepth;
        }
    }
    vec2 screenUv = (vec2(depthTexel) + 0.5) / vec2(depthSize);
    // Pull an exact camera-relative endpoint from opaque depth. The tiny sky clamp avoids an
    // infinite-view projection producing w=0; the march is capped to Farseer's reach below.
    float reconstructDepth = min(opaqueDepth, 0.999999);
    vec4 viewPoint = invProjection
        * vec4(screenUv * 2.0 - 1.0, reconstructDepth * 2.0 - 1.0, 1.0);
    outFog = vec4(0.0);
    outDistance = 0.0;
    if (abs(viewPoint.w) < 0.0000001) return;
    viewPoint /= viewPoint.w;
    vec3 relativeEndpoint = (invView * viewPoint).xyz;
    outDistance = length(relativeEndpoint);
    float endpointDistance = min(outDistance, farViewDistance);
    if (endpointDistance <= 0.001) return;
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
        || marchEnd <= max(0.0, marchStart)) return;

    float baseStep = mix(6.0, 2.5, clamp(fogLayerDetail, 0.0, 1.0));
    // Spatial dithering turns the residual fixed-step error into fine grain instead of coherent
    // view-facing bands. The field and density remain world anchored.
    float rayDistance = max(0.0, marchStart)
        + baseStep * vhFogHash(vec3(gl_FragCoord.xy, 0.731));
    float transmittance = 1.0;
    vec3 scattered = vec3(0.0);
    float conservativeReach = fogCoreBlocks + fogSpillBlocks * FarseerReachScale * 1.5
        + clamp(fogEdgeSoftness, 0.0, 1.0) * 0.5 * fogSpillBlocks + fieldTexelBlocks * 1.5;
    // The highest top is 1.3 heights and its ceiling ends at 1.15 of that.
    float bankCeiling = fogHeightBlocks * 1.5;

    // Sunlight. A sun below the horizon still lights from just above it while the day fades.
    vec3 sunDirection = normalize(vec3(sunPosition.x, max(sunPosition.y, 0.08), sunPosition.z));
    float sunHeight = max(0.25, sunDirection.y);
    float daylight = smoothstep(0.02, 0.35, dayLight);
    // Fog glows a little toward the sun and dims a little away from it.
    float phase = mix(1.0, vhFogPhase(dot(rayDirection, sunDirection), 0.3), 0.3);
    vec3 sunTint = sunColor / max(0.001, max(sunColor.r, max(sunColor.g, sunColor.b)));
    sunTint = mix(vec3(1.0), sunTint, 0.6 * daylight);
    float previousExtinction = 0.0;
    // Faint steps reuse the last lit colour instead of paying for their own lighting.
    vec3 litColour = rgbaFogIn.rgb * 0.85;

    for (int stepIndex = 0; stepIndex < MaxFogSteps; stepIndex++)
    {
        if (rayDistance >= marchEnd || transmittance < 0.01) break;
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
            heightAboveGround - bankCeiling));
        // A ray that follows the seam while above or below its local terrain must still advance;
        // otherwise it can spend the whole fixed budget walking empty air in tiny steps.
        float outside = max(horizontalOutside, verticalOutside * 0.35);
        float farStep = max(baseStep, rayDistance * FarStepFraction);
        // Inside the bank each segment stays optically thin, so a dense core cannot become a
        // stepped surface, while thin fog far away is crossed in longer strides.
        float stepBlocks = outside > 0.0
            ? clamp(outside * 0.72, baseStep, 512.0)
            : previousExtinction > 0.0
                ? clamp(0.35 / previousExtinction, 0.75, farStep) : farStep;
        stepBlocks = min(stepBlocks, marchEnd - rayDistance);
        previousExtinction = 0.0;

        if (horizontalOutside <= 0.0 && verticalOutside <= 0.0)
        {
            // Sample the middle of the segment. Fog thickening along it, as toward the ground,
            // is then neither under- nor over-counted however long the step grows.
            float middle = rayDistance + stepBlocks * 0.5;
            relative = rayDirection * middle;
            fieldPosition = cameraInField + relative;
            field = texture(fogField, clamp(fieldPosition.xz / vec2(fieldBlocksX, fieldBlocksZ),
                vec2(0.0), vec2(1.0)));
            globe = clamp((length(relative.xz) - globeStart)
                / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
            globeDrop = globeEffect * globe * globe * farViewDistance;
            heightAboveGround = fieldPosition.y - (field.g - globeDrop);
            // Noise is evaluated in the unwarped world so its scale and drift do not stretch as
            // the distant globe curve lowers geometry.
            vec3 fogWorldPosition = vec3(
                fieldWorldOriginX + fieldPosition.x,
                fieldPosition.y + globeDrop,
                fieldWorldOriginZ + fieldPosition.z);
            float pixelBlocks = max(0.001, middle * fogPixelScale);
            float top;
            float extinction = vhFogExtinction(fogWorldPosition, field, heightAboveGround,
                pixelBlocks, false, top);
            previousExtinction = extinction;
            if (extinction > 0.0000001)
            {
                float segmentAlpha = 1.0 - exp(-extinction * stepBlocks);
                float contribution = transmittance * segmentAlpha;
                transmittance *= 1.0 - segmentAlpha;
                // A step too faint to change the pixel alone skips the lighting work.
                if (contribution >= 0.0005)
                {
                    // Fog between this point and the sun shades it: one cheap look toward the sun,
                    // plus this point's own share. Without the look the bank goes flat grey.
                    float towardSun = extinction * max(1.0, ScaleHeightFraction * top) / sunHeight;
                    vec3 probePosition = fieldPosition + sunDirection * SunProbeBlocks;
                    vec2 probeUv = probePosition.xz / vec2(fieldBlocksX, fieldBlocksZ);
                    if (all(greaterThanEqual(probeUv, vec2(0.0)))
                        && all(lessThanEqual(probeUv, vec2(1.0))))
                    {
                        vec4 probeField = texture(fogField, probeUv);
                        float probeTop;
                        float probeExtinction = vhFogExtinction(
                            fogWorldPosition + sunDirection * SunProbeBlocks, probeField,
                            probePosition.y - (probeField.g - globeDrop), pixelBlocks, true,
                            probeTop);
                        towardSun = (extinction * 0.35 + probeExtinction) * SunProbeBlocks * 1.6;
                    }
                    // Beer's law plus a softer lobe standing in for light scattered many times.
                    float direct = (exp(-towardSun) + 0.6 * exp(-towardSun * 0.25)) / 1.6;
                    // Low fog sees less of the sky.
                    float sky = mix(0.55, 1.0,
                        clamp(max(0.0, heightAboveGround) / max(1.0, top), 0.0, 1.0));
                    float sunlit = 0.62 * direct * phase + 0.38 * sky;
                    float light = clamp(mix(sky, sunlit, daylight), 0.0, 1.2);
                    // Never brighter than the horizon fog; shadows turn cool grey-blue.
                    vec3 tint = mix(vec3(0.90, 0.94, 1.04), sunTint, clamp(light, 0.0, 1.0));
                    litColour = rgbaFogIn.rgb * mix(0.64, 0.93, light) * tint;
                }
                scattered += litColour * contribution;
            }
        }
        rayDistance += max(0.25, stepBlocks);
    }

    // Premultiplied. The cap keeps a trace of what lies behind even the densest fog.
    float opacity = 1.0 - transmittance;
    float alpha = min(opacity, 0.99);
    if (alpha < 0.002) return;
    outFog = vec4(scattered * (alpha / max(opacity, 0.0001)), alpha);
}
