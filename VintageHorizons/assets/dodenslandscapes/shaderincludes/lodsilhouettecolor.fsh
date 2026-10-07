// One colour model shared by the silhouette pass and cached terrain's outer fade target.
// Keeping it here prevents a join line caused by two nearly-identical copies drifting apart.
float vhSilhouetteBias(float value, float b)
{
    return pow(value, log(0.5) / log(b));
}

vec3 vhSilhouetteBias(vec3 value, float b)
{
    return pow(value, vec3(log(0.5) / log(b)));
}

void vhSilhouetteTerrainColor(vec3 position, float level, vec3 sunPos, vec3 sunlight,
    float daylight, float horizon, float fog, float skyTintValue, vec4 tint,
    float lightBias, float surfaceShade, out vec4 terrainColor, out vec4 terrainGlow)
{
    terrainColor = vec4(1.0);
    terrainGlow = vec4(1.0);
    float a = float(silhouetteSeaLevel) + 2.0;
    float b = float(silhouetteSeaLevel) - 1.0;
    float offset = skyTintValue + ((level - a) / (b - a)) * (-skyTintValue);
    getSkyColorAt(position, sunPos, offset, clamp(daylight, 0.0, 1.0), horizon,
        terrainColor, terrainGlow);
    terrainColor.rgb = mix(terrainColor.rgb, tint.rgb, tint.a);
    terrainColor.rgb *= vhSilhouetteBias(clamp(sunlight * daylight, 0.0, 1.0), lightBias);
    terrainColor.rgb *= surfaceShade;
    terrainColor = applyFog(terrainColor, fog);
    terrainColor = applySpheresFog(terrainColor, fog, position);
}
