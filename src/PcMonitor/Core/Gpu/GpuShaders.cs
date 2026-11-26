namespace PcMonitor.Core.Gpu;

/// <summary>
/// Набор HLSL-шейдеров для GPU-тестов. Все сцены рисуются в offscreen-текстуру,
/// поэтому частота кадров не ограничена частотой обновления монитора.
/// </summary>
internal static class GpuShaders
{
    public const string Common = """
        cbuffer Params : register(b0)
        {
            float Time;
            float Quality;
            float2 Resolution;
            float Frame;
            float3 Pad;
            float3 CameraPosition;
            float CameraYaw;
            float CameraPitch;
            float CameraMode;
            float2 CameraPad;
        };

        struct VSOut
        {
            float4 Position : SV_Position;
            float2 Uv : TEXCOORD0;
        };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut output;
            float2 pos = float2((id << 1) & 2, id & 2);
            output.Uv = pos;
            output.Position = float4(pos * float2(2, -2) + float2(-1, 1), 0, 1);
            return output;
        }

        float Hash(float2 p)
        {
            return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
        }

        float Noise(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            float a = Hash(i);
            float b = Hash(i + float2(1, 0));
            float c = Hash(i + float2(0, 1));
            float d = Hash(i + float2(1, 1));
            return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
        }

        float Fbm(float2 p, int octaves)
        {
            float value = 0.0;
            float amplitude = 0.5;
            for (int i = 0; i < octaves; i++)
            {
                value += amplitude * Noise(p);
                p *= 2.02;
                amplitude *= 0.5;
            }
            return value;
        }
        """;

    /// <summary>Свободная камера: настоящая ходьба/полёт по процедурной комнате с коллизией пола.</summary>
    public const string FreeCam = """
        float MapRoom(float3 p)
        {
            float floorD = p.y;
            float leftWall = p.x + 5.0;
            float rightWall = 5.0 - p.x;
            float backWall = p.z - 12.0;
            float frontWall = -p.z - 8.0;
            float d = max(max(max(floorD, leftWall), rightWall), max(backWall, frontWall));
            float sphere = length(p - float3(0.0, 1.0, 2.0)) - 1.0;
            return min(d, sphere);
        }
        float3 RoomNormal(float3 p)
        {
            float e = 0.002;
            return normalize(float3(MapRoom(p + float3(e,0,0)) - MapRoom(p - float3(e,0,0)), MapRoom(p + float3(0,e,0)) - MapRoom(p - float3(0,e,0)), MapRoom(p + float3(0,0,e)) - MapRoom(p - float3(0,0,e))));
        }
        float4 PSFreeCam(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;
            float cy = cos(CameraYaw), sy = sin(CameraYaw), cp = cos(CameraPitch), sp = sin(CameraPitch);
            float3 forward = normalize(float3(sy * cp, sp, cy * cp));
            float3 right = normalize(cross(forward, float3(0,1,0)));
            float3 up = cross(right, forward);
            float3 ro = CameraPosition;
            float3 rd = normalize(forward + right * uv.x * 0.72 + up * uv.y * 0.72);
            float t = 0.0; float hit = 0.0;
            for (int i = 0; i < 96; i++) { float d = MapRoom(ro + rd * t); if (d < 0.002) { hit = 1.0; break; } t += max(0.01, d); if (t > 40.0) break; }
            float3 color = float3(0.015, 0.02, 0.03) + float3(0.02, 0.035, 0.06) * (1.0 - uv.y);
            if (hit > 0.5) { float3 p = ro + rd * t; float3 n = RoomNormal(p); float3 lightPos = float3(0,4,3); float3 l = normalize(lightPos-p); float diff = saturate(dot(n,l)); float grid = abs(frac(p.x)-0.5)+abs(frac(p.z)-0.5); float3 base = p.y < 0.01 ? float3(0.12,0.15,0.18) : (p.y > 4.98 ? float3(0.08,0.1,0.14) : float3(0.16,0.18,0.22)); base += float3(0.08,0.25,0.6) * exp(-abs(p.x) * 0.5) * (1.0-step(0.06, grid)); color = base * (0.12 + diff * 0.95); color += float3(1.0,0.18,0.04) * exp(-length(p-float3(0,2,4))*1.8); }
            return float4(saturate(color), 1.0);
        }
        """;

    /// <summary>Реймарчинг фрактала (Mandelbulb) — тяжёлая вычислительная сцена.</summary>
    public const string Raymarch = """
        float MandelbulbDistance(float3 position, float power, int iterations)
        {
            float3 z = position;
            float derivative = 1.0;
            float radius = 0.0;

            for (int i = 0; i < iterations; i++)
            {
                radius = length(z);
                if (radius > 2.0) break;

                float theta = acos(clamp(z.z / max(radius, 1e-6), -1.0, 1.0));
                float phi = atan2(z.y, z.x);
                derivative = pow(radius, power - 1.0) * power * derivative + 1.0;
                float scaledRadius = pow(radius, power);
                theta *= power;
                phi *= power;
                z = scaledRadius * float3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta)) + position;
            }

            return 0.5 * log(max(radius, 1e-6)) * radius / max(derivative, 1e-6);
        }

        float4 PSRaymarch(VSOut input) : SV_Target
        {
            float2 uv = (input.Uv * 2.0 - 1.0);
            uv.x *= Resolution.x / Resolution.y;

            float angle = Time * 0.35;
            float3 origin = float3(sin(angle) * 2.6, 1.5 + 0.4 * sin(Time * 0.2), cos(angle) * 2.6);
            float3 target = float3(0, 0, 0);
            float3 forward = normalize(target - origin);
            float3 right = normalize(cross(forward, float3(0, 1, 0)));
            float3 up = cross(right, forward);
            float3 direction = normalize(forward + right * uv.x * 0.7 + up * uv.y * 0.7);

            int iterations = (int)lerp(6.0, 16.0, Quality);
            float power = lerp(6.0, 9.0, Quality);
            int steps = (int)lerp(70.0, 190.0, Quality);

            float distance = 0.0;
            float accumulated = 0.0;
            float3 position = origin;

            for (int step = 0; step < steps; step++)
            {
                position = origin + direction * distance;
                float d = MandelbulbDistance(position, power, iterations);
                if (d < 0.0006)
                {
                    accumulated = 1.0;
                    break;
                }

                distance += d * 0.85;
                if (distance > 6.0) break;
            }

            if (accumulated < 0.5)
                return float4(0.02, 0.03, 0.06, 1.0);

            float glow = exp(-distance * 0.35);
            float3 color = 0.55 + 0.45 * cos(6.0 * distance + float3(0.0, 2.0, 4.0) + Time * 0.4);
            color *= glow;
            return float4(color, 1.0);
        }
        """;

    /// <summary>Планета с кольцами: реймарчинг сферы, процедурные полосы, тени колец.</summary>
    public const string Planet = """
        float4 PSPlanet(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            // Камера облетает планету по орбите: сквозь неё не пролетаем.
            float angle = Time * 0.09;
            float distance = 3.9 + 0.35 * sin(Time * 0.07);
            float3 ro = float3(sin(angle) * distance, 0.85 + 0.25 * sin(Time * 0.11), -cos(angle) * distance);
            float3 forward = normalize(-ro);
            float3 right = normalize(cross(float3(0.0, 1.0, 0.0), forward));
            float3 up = cross(forward, right);
            float3 rd = normalize(forward + right * uv.x * 0.62 + up * uv.y * 0.62);
            float3 light = normalize(float3(0.55, 0.35, -0.75));

            int octaves = (int)lerp(6.0, 12.0, Quality);

            // Туманность на весь экран: тяжёлый шум для каждого пикселя.
            float3 nebula = 0.0;
            float nebulaDensity = Fbm(uv * 1.8 + float2(Time * 0.01, 0.0), octaves);
            float nebulaDetail = Fbm(uv * 5.5 - float2(0.0, Time * 0.015), max(3, octaves / 2));
            nebula = lerp(float3(0.06, 0.03, 0.14), float3(0.28, 0.12, 0.42), nebulaDensity);
            nebula = lerp(nebula, float3(0.35, 0.5, 0.85), smoothstep(0.55, 0.95, nebulaDetail) * 0.6);
            nebula *= 0.35 + 0.65 * smoothstep(0.25, 0.85, nebulaDensity);

            // Звёздное небо.
            float3 color = nebula;
            float2 grid = floor(input.Uv * Resolution * 0.55);
            float star = Hash(grid);
            if (star > 0.9975)
                color += float3(0.75, 0.82, 1.0) * (star - 0.9975) * 260.0;

            // Сфера планеты.
            float radius = 1.0;
            float b = dot(ro, rd);
            float c = dot(ro, ro) - radius * radius;
            float disc = b * b - c;
            float planetT = 1e9;
            if (disc > 0.0)
            {
                float t = -b - sqrt(disc);
                if (t > 0.0)
                {
                    planetT = t;
                    float3 p = ro + rd * t;
                    float3 n = normalize(p);

                    float bands = Fbm(float2(p.y * 5.5, p.x * 1.4 + Time * 0.05), octaves);
                    float detail = Fbm(float2(p.y * 18.0, p.z * 6.0), max(3, octaves / 2));
                    float3 surface = lerp(float3(0.78, 0.63, 0.42), float3(0.32, 0.22, 0.15), bands);
                    surface = lerp(surface, float3(0.92, 0.84, 0.66), smoothstep(0.58, 0.78, bands));
                    surface = lerp(surface, float3(0.85, 0.55, 0.35), smoothstep(0.6, 0.95, detail) * 0.35);

                    float diffuse = saturate(dot(n, light));
                    float terminator = smoothstep(-0.12, 0.42, diffuse);
                    color = surface * (0.04 + 1.25 * terminator);
                    color += float3(0.25, 0.4, 0.9) * pow(saturate(dot(n, -rd)), 2.0) * 0.12;
                }
            }

            // Кольца в плоскости y = 0.
            if (abs(rd.y) > 1e-4)
            {
                float ringT = -ro.y / rd.y;
                if (ringT > 0.0 && ringT < planetT)
                {
                    float3 rp = ro + rd * ringT;
                    float ringRadius = length(rp.xz);
                    if (ringRadius > 1.35 && ringRadius < 2.45)
                    {
                        float band = Fbm(float2(ringRadius * 9.0, 0.0), 5);
                        float alpha = saturate(band * 1.5 - 0.25) * smoothstep(2.45, 2.05, ringRadius) *
                                      smoothstep(1.35, 1.65, ringRadius);
                        float3 ringColor = lerp(float3(0.55, 0.5, 0.42), float3(0.95, 0.9, 0.78), band);
                        color = lerp(color, ringColor, alpha * 0.85);
                    }
                }
            }

            color = color / (1.0 + color * 0.55);
            return float4(pow(saturate(color), 0.9), 1.0);
        }
        """;

    /// <summary>Закат над водой: небо, солнце и отражение с волнами.</summary>
    public const string Sunset = """
        float3 SunsetSky(float3 rd, float3 light)
        {
            float up = saturate(rd.y * 1.4 + 0.1);
            float3 horizon = float3(1.0, 0.45, 0.18);
            float3 zenith = float3(0.06, 0.12, 0.35);
            float3 sky = lerp(horizon, zenith, pow(up, 0.55));

            float sunDot = saturate(dot(rd, light));
            sky += float3(1.0, 0.72, 0.35) * pow(sunDot, 900.0) * 12.0;
            sky += float3(1.0, 0.45, 0.15) * pow(sunDot, 24.0) * 0.55;

            // Облачные полосы.
            float clouds = Fbm(float2(rd.x / max(0.12, rd.y) * 0.35, rd.z / max(0.12, rd.y) * 0.35 + Time * 0.01), 5);
            float mask = smoothstep(0.45, 0.85, clouds) * saturate(rd.y * 3.0);
            sky = lerp(sky, float3(1.0, 0.6, 0.4), mask * 0.45);
            return sky;
        }

        float4 PSSunset(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.y = -uv.y;
            uv.x *= Resolution.x / Resolution.y;

            float3 light = normalize(float3(0.25, 0.09, 0.95));
            float3 ro = float3(0.0, 1.35, -6.0 + Time * 0.5);
            float3 rd = normalize(float3(uv.x * 0.72, uv.y * 0.5 - 0.12, 1.0));

            int octaves = (int)lerp(4.0, 9.0, Quality);
            float3 color;

            if (rd.y < 0.0)
            {
                // Пересечение с водой: волны и отражение солнца.
                float t = -ro.y / rd.y;
                float3 p = ro + rd * t;
                float wave = Fbm(p.xz * 0.55 + float2(Time * 0.35, Time * 0.22), octaves);
                float wave2 = Fbm(p.xz * 2.3 - float2(Time * 0.5, 0.0), max(2, octaves / 2));
                float3 normal = normalize(float3(
                    (wave - Fbm((p.xz + float2(0.22, 0)) * 0.55 + float2(Time * 0.35, Time * 0.22), octaves)) * 3.0,
                    1.0,
                    (wave - Fbm((p.xz + float2(0, 0.22)) * 0.55 + float2(Time * 0.35, Time * 0.22), octaves)) * 3.0));

                float3 reflected = reflect(rd, normal);
                reflected.y = abs(reflected.y);
                float3 sky = SunsetSky(reflected, light);
                float fresnel = pow(1.0 - saturate(dot(-rd, normal)), 3.0);

                float3 water = lerp(float3(0.02, 0.05, 0.09), sky, 0.35 + 0.65 * fresnel);
                water += float3(1.0, 0.5, 0.2) * pow(saturate(dot(reflected, light)), 220.0) * 9.0;
                water += float3(0.9, 0.95, 1.0) * pow(wave2, 6.0) * 0.12;

                float fade = 1.0 - smoothstep(-0.35, -0.02, rd.y);
                color = lerp(SunsetSky(rd, light), water, fade);
            }
            else
            {
                color = SunsetSky(rd, light);
            }

            color = color / (1.0 + color * 0.35);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Ночной город с перспективной улицей, кварталами и оконным светом.</summary>
    public const string City = """
        float CityMap(float3 p, out float3 emissive, out int material)
        {
            emissive = 0.0;
            material = 0;

            float distance = p.y;
            material = 1;

            const float blockX = 7.0;
            const float blockZ = 9.0;
            float2 cell = floor(float2(p.x / blockX, p.z / blockZ));
            float2 center = (cell + 0.5) * float2(blockX, blockZ);
            float seed = Hash(cell + 17.3);
            float width = 3.1 + Hash(cell + 5.7) * 1.2;
            float depth = 5.1 + Hash(cell + 8.9) * 1.5;
            float height = 2.2 + pow(Hash(cell + 3.1), 0.75) * 7.0;
            float3 q = abs(p - float3(center.x, height * 0.5, center.y)) - float3(width * 0.5, height * 0.5, depth * 0.5);
            float buildingDistance = length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);

            if (seed > 0.16 && buildingDistance < distance)
            {
                distance = buildingDistance;
                material = 2;

                float distanceXFace = abs(abs(p.x - center.x) - width * 0.5);
                float distanceZFace = abs(abs(p.z - center.y) - depth * 0.5);
                bool zFace = distanceZFace < distanceXFace;
                bool facade = min(distanceXFace, distanceZFace) < 0.05;
                float horizontal = zFace ? p.x - center.x : p.z - center.y;
                float2 windowGrid = float2(horizontal * 1.65, p.y * 1.45);
                float2 facadeCell = floor(windowGrid);
                float2 windowUv = frac(windowGrid) - 0.5;
                float windowMask = step(max(abs(windowUv.x), abs(windowUv.y)), 0.25) * (facade ? 1.0 : 0.0);
                float lit = step(0.66, Hash(facadeCell + cell * 13.7));
                float3 tint = lerp(float3(1.0, 0.64, 0.28), float3(0.45, 0.72, 1.0), Hash(facadeCell + 4.1));
                emissive = tint * windowMask * lit * (0.35 + Hash(facadeCell + 9.2) * 0.5);
            }

            return distance;
        }

        float4 PSCity(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.y = -uv.y;
            uv.x *= Resolution.x / Resolution.y;

            float3 ro = float3(0.0, 2.0, -18.0 + Time * 1.15);
            float3 rd = normalize(float3(uv.x * 0.72, uv.y * 0.54 - 0.16, 1.0));
            float3 sky = lerp(float3(0.012, 0.018, 0.045), float3(0.11, 0.16, 0.28), saturate(rd.y * 1.7 + 0.15));

            int steps = (int)lerp(48.0, 120.0, Quality);
            float travel = 0.25;
            float3 color = sky;

            for (int i = 0; i < steps; i++)
            {
                float3 p = ro + rd * travel;
                float3 emissive;
                int material;
                float distance = CityMap(p, emissive, material);

                if (distance < 0.006)
                {
                    float3 emissionAtHit = emissive;
                    int materialAtHit = material;
                    float3 normal = normalize(float3(
                        CityMap(p + float3(0.02, 0, 0), emissive, material) - CityMap(p - float3(0.02, 0, 0), emissive, material),
                        CityMap(p + float3(0, 0.02, 0), emissive, material) - CityMap(p - float3(0, 0.02, 0), emissive, material),
                        CityMap(p + float3(0, 0, 0.02), emissive, material) - CityMap(p - float3(0, 0, 0.02), emissive, material)));
                    float diffuse = 0.25 + 0.75 * saturate(dot(normal, normalize(float3(-0.35, 0.8, -0.4))));
                    if (materialAtHit == 1)
                    {
                        float lane = step(abs(frac(p.z * 0.25) - 0.5), 0.025) * step(abs(p.x), 0.09);
                        color = float3(0.035, 0.042, 0.058) + lane * float3(0.45, 0.36, 0.2);
                    }
                    else
                    {
                        float3 facade = float3(0.12, 0.14, 0.21) * diffuse;
                        color = facade + emissionAtHit * 1.4;
                    }
                    break;
                }

                travel += max(0.025, distance * 0.82);
                if (travel > 90.0) break;
            }

            float fog = 1.0 - exp(-travel * 0.027);
            color = lerp(color, sky, fog);
            color = color / (1.0 + color * 0.35);
            return float4(pow(saturate(color), 0.9), 1.0);
        }
        """;

    /// <summary>Бесконечный тоннель с неоновой сеткой.</summary>
    public const string Tunnel = """
        float4 PSTunnel(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            float radius = length(uv) + 0.0001;
            float angle = atan2(uv.y, uv.x);

            float depth = 0.65 / radius + Time * 2.2;
            float3 color = 0.0;

            // Неоновые кольца и спицы, летящие навстречу.
            float rings = sin(depth * 6.0) * 0.5 + 0.5;
            rings = pow(rings, 6.0);

            float spokes = sin(angle * 12.0 + depth * 1.6) * 0.5 + 0.5;
            spokes = pow(spokes, 5.0);

            float3 ringTint = 0.5 + 0.5 * cos(depth * 0.9 + float3(0.0, 2.1, 4.2));
            color += ringTint * rings * (0.55 + 0.85 * Quality);
            color += float3(0.35, 0.75, 1.0) * spokes * 0.35;

            // Дальний свет в конце тоннеля.
            color += float3(0.55, 0.75, 1.0) / (1.0 + radius * 22.0) * 0.9;

            // Мелкая структура по глубине (проверка ALU).
            int steps = (int)lerp(6.0, 28.0, Quality);
            float detail = 0.0;
            for (int i = 0; i < steps; i++)
            {
                float offset = (float)i * 0.21;
                detail += Fbm(float2(angle * 3.0 + offset, depth * 0.7 + offset), 2) / (float)(i + 2);
            }
            color *= 0.55 + detail * 1.5;

            color = color / (1.0 + color * 0.6);
            return float4(pow(saturate(color), 0.8), 1.0);
        }
        """;

    /// <summary>Спиральная галактика из десятков тысяч частиц.</summary>
    public const string Galaxy = """
        float4 PSGalaxy(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            int count = (int)lerp(120.0, 1200.0, Quality);
            float3 color = 0.0;

            for (int index = 0; index < count; index++)
            {
                float f = (float)index;
                float seed = Hash(float2(f, 3.7));
                float seed2 = Hash(float2(f, 11.3));

                // Спиральные рукава.
                float arm = floor(seed * 2.0);
                float radius = 0.05 + pow(seed2, 0.65) * 1.5;
                float baseAngle = radius * 2.6 + arm * 3.14159;
                float angle = baseAngle + Time * (0.35 / (0.35 + radius));
                float scatter = (Hash(float2(f, 7.1)) - 0.5) * (0.22 + radius * 0.35);

                float height = (Hash(float2(f, 5.5)) - 0.5) * (0.35 - radius * 0.16);

                float2 position = float2(cos(angle), sin(angle)) * radius;
                position += normalize(float2(-position.y, position.x) + 1e-5) * scatter;

                float2 delta = uv - position;
                delta.y -= height * 0.4;
                float distance = length(delta);
                float size = 0.006 + 0.02 * Hash(float2(f, 13.7));

                float glow = size / (distance * distance + 0.00009);

                // Ядро — тёплое, рукава — холодные.
                float3 tint = lerp(float3(1.0, 0.82, 0.5), float3(0.45, 0.62, 1.0), saturate(radius * 0.85));
                tint = lerp(tint, float3(0.85, 0.45, 0.95), saturate(scatter * 3.0));
                color += tint * glow;
            }

            // Яркое ядро и фон.
            color += float3(1.0, 0.9, 0.7) / (1.0 + dot(uv, uv) * 240.0) * 1.4;
            color += float3(0.01, 0.012, 0.03);

            color = color / (1.0 + color * 0.35);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Бесконечный зум фрактала Мандельброта со сглаженной раскраской.</summary>
    public const string Fractal = """
        float4 PSFractal(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            // Бесконечный зум к интересной точке множества.
            float zoom = pow(1.35, Time * 1.15);
            float2 center = float2(-0.743643887037151, 0.13182590420533);
            float2 c = center + uv * (1.35 / zoom);

            int iterations = (int)lerp(90.0, 620.0, Quality);
            float2 z = 0.0;
            float escape = 0.0;
            int step = 0;

            for (int i = 0; i < iterations; i++)
            {
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
                if (dot(z, z) > 256.0)
                {
                    escape = (float)i;
                    step = i;
                    break;
                }
            }

            float3 color;
            if (escape > 0.0)
            {
                float logZ = log(dot(z, z)) * 0.5;
                float smoothIteration = escape + 1.0 - log(logZ) / log(2.0);
                float shade = frac(smoothIteration * 0.045);
                color = 0.5 + 0.5 * cos(6.2831 * (shade + float3(0.0, 0.33, 0.67)));
                color *= 0.35 + 0.65 * pow(shade, 0.6);
            }
            else
            {
                color = 0.0;
            }

            color = color / (1.0 + color * 0.25);
            return float4(pow(saturate(color), 0.9), 1.0);
        }
        """;

    /// <summary>Трассировка лучей: зеркальные, стеклянные и матовые сферы, несколько отражений на пиксель.</summary>
    public const string RayTrace = """
        float3 TraceScene(float3 ro, float3 rd, int bounces, int seed, out float3 throughput)
        {
            float3 color = 0.0;
            float3 mask = 1.0;

            for (int bounce = 0; bounce < bounces; bounce++)
            {
                // Ближайшее пересечение с тремя сферами и полом.
                float best = 1e9;
                int hit = -1;

                float3 centers[3] = { float3(-1.15, 0.55, 0.0), float3(0.0, 0.55, -0.35), float3(1.15, 0.55, 0.15) };
                float radii[3] = { 0.55, 0.55, 0.55 };

                for (int i = 0; i < 3; i++)
                {
                    float b = dot(ro - centers[i], rd);
                    float c = dot(ro - centers[i], ro - centers[i]) - radii[i] * radii[i];
                    float disc = b * b - c;
                    if (disc > 0.0)
                    {
                        float t = -b - sqrt(disc);
                        if (t > 0.001 && t < best)
                        {
                            best = t;
                            hit = i;
                        }
                    }
                }

                // Пол.
                if (abs(rd.y) > 1e-4)
                {
                    float t = -ro.y / rd.y;
                    if (t > 0.001 && t < best)
                    {
                        best = t;
                        hit = 3;
                    }
                }

                if (hit < 0) break;

                float3 p = ro + rd * best;
                float3 n;
                float3 material;

                if (hit == 3)
                {
                    // Шахматный пол и мягкая тень от сфер.
                    float2 cell = floor(p.xz * 2.0);
                    float chess = frac((cell.x + cell.y) * 0.5) < 0.5 ? 0.55 : 0.2;
                    n = float3(0.0, 1.0, 0.0);
                    material = float3(chess, chess, chess);

                    float shadow = 1.0;
                    for (int i = 0; i < 3; i++)
                    {
                        float3 toSphere = centers[i] - p;
                        float distance = length(toSphere);
                        float dotNL = dot(normalize(toSphere), float3(0.5, 0.8, 0.35));
                        shadow *= saturate(distance - 0.55);
                        shadow *= saturate(dotNL + 0.35);
                    }
                    material *= 0.45 + 0.55 * saturate(shadow);
                }
                else
                {
                    n = normalize(p - centers[hit]);
                    material = hit == 0 ? float3(1.0, 1.0, 1.0) : hit == 1 ? float3(0.85, 0.2, 0.2) : float3(0.2, 0.45, 0.95);
                }

                float3 light = normalize(float3(0.5, 0.85, 0.3));
                float diffuse = saturate(dot(n, light));

                // Материалы: 0 — зеркало, 1 — стекло, 2 — матовый, 3 — пол.
                if (hit == 0)
                {
                    color += mask * material * (0.15 + 0.35 * diffuse);
                    mask *= 0.75;
                    rd = reflect(rd, n);
                }
                else if (hit == 1)
                {
                    float ior = 1.45;
                    float3 refracted = refract(rd, n, 1.0 / ior);
                    if (dot(refracted, refracted) < 0.001) refracted = reflect(rd, n);
                    color += mask * material * (0.25 + 0.5 * diffuse);
                    mask *= 0.85;
                    rd = normalize(refracted + 0.35 * float3(Hash(float2(seed, bounce)), Hash(float2(bounce, seed)), 0.0));
                }
                else
                {
                    color += mask * material * (0.1 + 0.9 * diffuse);
                    mask *= 0.55;
                }

                ro = p + n * 0.002;
            }

            throughput = mask;
            return color;
        }

        float4 PSRayTrace(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.y = -uv.y;
            uv.x *= Resolution.x / Resolution.y;

            float3 ro = float3(0.0, 1.35, -4.2 + Time * 0.25);
            float3 rd = normalize(float3(uv.x * 0.85, uv.y * 0.6 - 0.15, 1.2));

            // Каждый кадр считаем заново со случайным смещением: получается сглаживание шума.
            float2 jitter = float2(Hash(float2(Frame, 1.7)), Hash(float2(Frame, 5.3))) - 0.5;
            rd = normalize(rd + float3(jitter * 0.0025, jitter.x * 0.0025));

            int bounces = (int)lerp(3.0, 9.0, Quality);
            int samples = (int)lerp(1.0, 3.0, Quality);

            float3 color = 0.0;
            for (int sample = 0; sample < samples; sample++)
            {
                float3 throughput;
                color += TraceScene(ro, rd, bounces, sample + (int)Frame, throughput);
            }

            color /= (float)samples;

            // Небо как источник света.
            float3 sky = lerp(float3(0.05, 0.07, 0.12), float3(0.35, 0.45, 0.7), saturate(rd.y * 2.0 + 0.4));
            color += sky * 0.25;

            color = color / (1.0 + color * 0.7);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Мех: слои ворса вокруг тора — очень тяжёлая нагрузка на заливку (в духе GPU).</summary>
    public const string Fur = """
        float TorusDistance(float3 p, float2 radii)
        {
            float2 q = float2(length(p.xz) - radii.x, p.y);
            return length(q) - radii.y;
        }

        float FurDistance(float3 p, float2 radii)
        {
            float baseDistance = TorusDistance(p, radii);
            float grain = Fbm(p.xz * 24.0 + float2(p.y * 8.0 + Time * 0.16, p.y * 13.0), 4);
            return baseDistance - (grain - 0.5) * 0.11;
        }

        float4 PSFur(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.y = -uv.y;
            uv.x *= Resolution.x / Resolution.y;

            float3 ro = float3(0.0, 0.48, -3.55 + 0.28 * sin(Time * 0.2));
            float3 rd = normalize(float3(uv.x * 0.74, uv.y * 0.6 - 0.02, 1.2));

            float2 radii = float2(1.08, 0.39);
            int steps = (int)lerp(48.0, 150.0, Quality);

            float travel = 0.2;
            bool hitSurface = false;
            float3 position = 0.0;

            for (int i = 0; i < steps; i++)
            {
                position = ro + rd * travel + float3(0.0, sin(Time * 0.7) * 0.05, 0.0);
                float distance = FurDistance(position, radii);
                if (distance < 0.002)
                {
                    hitSurface = true;
                    break;
                }

                travel += distance * 0.85;
                if (travel > 12.0) break;
            }

            if (!hitSurface)
                return float4(0.02, 0.02, 0.03, 1.0);

            // Слои ворса: для каждого слоя считаем затенение и накапливаем прозрачность.
            int layers = (int)lerp(18.0, 64.0, Quality);
            float epsilon = 0.008;
            float3 normal = normalize(float3(
                FurDistance(position + float3(epsilon, 0, 0), radii) - FurDistance(position - float3(epsilon, 0, 0), radii),
                FurDistance(position + float3(0, epsilon, 0), radii) - FurDistance(position - float3(0, epsilon, 0), radii),
                FurDistance(position + float3(0, 0, epsilon), radii) - FurDistance(position - float3(0, 0, epsilon), radii)));

            float3 lightDirection = normalize(float3(0.6, 1.0, -0.4));
            float3 color = 0.0;
            float alpha = 0.0;

            for (int layer = 0; layer < layers; layer++)
            {
                float t = (float)layer / (float)layers;
                float3 shell = position + normal * t * 0.25;
                float fur = Fbm(shell.xz * 34.0 + float2(shell.y * 9.0, Time * 0.15), 4);
                float angle = atan2(shell.y, length(shell.xz) - radii.x);
                float fiber = pow(1.0 - abs(sin(angle * 78.0 + fur * 8.0 + shell.x * 5.0)), 9.0);

                float diffuse = 0.2 + 0.8 * saturate(dot(normal, lightDirection));
                float3 tint = lerp(float3(0.18, 0.075, 0.035), float3(0.88, 0.55, 0.24), saturate(fur * 0.85 + t * 0.22));
                color += tint * diffuse * (0.12 + 0.28 * fiber) * (1.0 - alpha);
                alpha += (0.09 + 0.08 * fiber) * (1.0 - alpha);
                if (alpha > 0.985) break;
            }

            color += float3(0.05, 0.05, 0.07);
            color = color / (1.0 + color * 0.5);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Зал с отражениями: зеркальный пол, светящиеся полосы, туман (в духе сводного бенчмарка).</summary>
    public const string Hall = """
        float HallMap(float3 p, out float3 emissive)
        {
            emissive = 0.0;

            // Пол и потолок.
            float floorDistance = p.y + 1.0;
            float ceilingDistance = 3.2 - p.y;

            // Стены по бокам.
            float wallDistance = 2.6 - abs(p.x);

            // Колонны вдоль зала.
            float3 cell = float3(0.0, 0.0, floor(p.z / 4.0) * 4.0);
            float pillar = length(float2(abs(p.x) - 2.2, p.y - 1.1)) - 0.35;

            float distance = min(min(floorDistance, ceilingDistance), min(wallDistance, pillar));

            // Светящиеся полосы на потолке и стенах.
            float ceilingStrip = step(abs(frac(p.z / 2.0) - 0.5), 0.12) * step(2.85, p.y);
            float wallStrip = step(abs(frac(p.z / 2.0) - 0.5), 0.08) * step(2.3, abs(p.x));
            emissive = float3(0.35, 0.6, 1.0) * ceilingStrip * 1.4 + float3(0.9, 0.35, 0.6) * wallStrip * 0.9;

            return distance;
        }

        float4 PSHall(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            float3 ro = float3(0.0, 0.55, -12.0 + Time * 3.2);
            float3 rd = normalize(float3(uv.x * 0.8, uv.y * 0.55 + 0.02, 1.0));

            int steps = (int)lerp(56.0, 170.0, Quality);
            float travel = 0.1;
            float3 color = float3(0.01, 0.012, 0.02);
            float3 glow = 0.0;
            bool hit = false;

            for (int i = 0; i < steps; i++)
            {
                float3 p = ro + rd * travel;
                float3 emissive;
                float distance = HallMap(p, emissive);
                glow += emissive * exp(-distance * 3.0) * 0.05;

                if (distance < 0.004)
                {
                    hit = true;
                    float3 normal = normalize(float3(
                        HallMap(p + float3(0.01, 0, 0), emissive) - HallMap(p - float3(0.01, 0, 0), emissive),
                        HallMap(p + float3(0, 0.01, 0), emissive) - HallMap(p - float3(0, 0.01, 0), emissive),
                        HallMap(p + float3(0, 0, 0.01), emissive) - HallMap(p - float3(0, 0, 0.01), emissive)));

                    float3 material = p.y < -0.9 ? float3(0.08, 0.09, 0.11) : float3(0.05, 0.05, 0.06);
                    float3 reflected = reflect(rd, normal);
                    float fresnel = pow(1.0 - saturate(dot(-rd, normal)), 4.0);

                    // Отражение: повторный короткий проход по лучу отражения.
                    float3 reflection = float3(0.02, 0.03, 0.06);
                    float reflectionTravel = 0.2;
                    for (int r = 0; r < steps / 2; r++)
                    {
                        float3 rp = p + reflected * reflectionTravel;
                        float3 re;
                        float rdistance = HallMap(rp, re);
                        reflection += re * exp(-rdistance * 2.5) * 0.06;
                        if (rdistance < 0.01) break;
                        reflectionTravel += max(0.05, rdistance * 0.9);
                        if (reflectionTravel > 30.0) break;
                    }

                    color = lerp(material, reflection, 0.35 + 0.6 * fresnel);
                    break;
                }

                travel += max(0.02, distance * 0.9);
                if (travel > 45.0) break;
            }

            color += glow * 0.7;
            color += float3(0.02, 0.03, 0.06) * (1.0 - exp(-travel * 0.05));
            color = color / (1.0 + color * 0.5);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Комната с фигурами: глянцевые материалы, отражения и мягкие тени (в духе сцены отражений).</summary>
    public const string Room = """
        float RoomMap(float3 p, out float3 emissive, out int material)
        {
            emissive = 0.0;
            material = 0;

            float floorDistance = p.y;
            float ceilingDistance = 3.6 - p.y;
            float backWall = p.z + 3.0;
            float leftWall = p.x + 4.0;
            float rightWall = 4.0 - p.x;

            float distance = min(min(floorDistance, ceilingDistance), min(min(backWall, leftWall), rightWall));
            material = 0;

            // Красный куб слева.
            float3 cube = abs(p - float3(-1.7, 0.7, -0.6)) - float3(0.7, 0.7, 0.7);
            float cubeDistance = length(max(cube, 0.0)) + min(max(cube.x, max(cube.y, cube.z)), 0.0);
            if (cubeDistance < distance)
            {
                distance = cubeDistance;
                material = 1;
            }

            // Синяя сфера справа.
            float sphereDistance = length(p - float3(1.8, 0.85, -1.2)) - 0.85;
            if (sphereDistance < distance)
            {
                distance = sphereDistance;
                material = 2;
            }

            // Медный тор в центре.
            float2 q = float2(length((p - float3(0.0, 0.75, 0.6)).xz) - 0.65, (p - float3(0.0, 0.75, 0.6)).y);
            float torusDistance = length(q) - 0.26;
            if (torusDistance < distance)
            {
                distance = torusDistance;
                material = 3;
            }

            // Световая панель на потолке.
            float panel = step(abs(p.x) - 1.2, 0.0) * step(abs(p.z + 1.4) - 1.0, 0.0) * step(3.55, p.y);
            emissive = float3(1.0, 0.96, 0.9) * panel * 3.0;

            return distance;
        }

        float4 PSRoom(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.y = -uv.y;
            uv.x *= Resolution.x / Resolution.y;

            float3 ro = float3(0.55 * sin(Time * 0.25), 1.5, 4.6 - 0.5 * cos(Time * 0.18));
            float3 target = float3(0.0, 0.9, -0.8);
            float3 forward = normalize(target - ro);
            float3 right = normalize(cross(float3(0.0, 1.0, 0.0), forward));
            float3 up = cross(forward, right);
            float3 rd = normalize(forward + right * uv.x * 0.62 + up * uv.y * 0.5);

            int steps = (int)lerp(64.0, 200.0, Quality);
            float travel = 0.1;
            float3 color = float3(0.012, 0.012, 0.016);
            float3 glow = 0.0;

            for (int i = 0; i < steps; i++)
            {
                float3 p = ro + rd * travel;
                float3 emissive;
                int material;
                float distance = RoomMap(p, emissive, material);

                glow += emissive * exp(-distance * 4.0) * 0.03;

                if (distance < 0.003)
                {
                    float3 normal = normalize(float3(
                        RoomMap(p + float3(0.01, 0, 0), emissive, material) - RoomMap(p - float3(0.01, 0, 0), emissive, material),
                        RoomMap(p + float3(0, 0.01, 0), emissive, material) - RoomMap(p - float3(0, 0.01, 0), emissive, material),
                        RoomMap(p + float3(0, 0, 0.01), emissive, material) - RoomMap(p - float3(0, 0, 0.01), emissive, material)));

                    float3 light = normalize(float3(0.2, 1.0, -0.35));
                    float diffuse = saturate(dot(normal, light));

                    float3 material0 = float3(0.72, 0.72, 0.74);
                    float3 albedo = material == 1 ? float3(0.85, 0.18, 0.16)
                        : material == 2 ? float3(0.18, 0.35, 0.9)
                        : material == 3 ? float3(0.9, 0.55, 0.3)
                        : float3(0.78, 0.78, 0.8);

                    float3 reflected = reflect(rd, normal);
                    float3 reflection = lerp(float3(0.025, 0.035, 0.06), float3(0.11, 0.14, 0.2), saturate(reflected.y));
                    float reflectionTravel = 0.025;
                    for (int bounce = 0; bounce < steps / 2; bounce++)
                    {
                        float3 reflectedPoint = p + normal * 0.012 + reflected * reflectionTravel;
                        float3 reflectedEmission;
                        int reflectedMaterial;
                        float reflectedDistance = RoomMap(reflectedPoint, reflectedEmission, reflectedMaterial);
                        if (reflectedDistance < 0.008)
                        {
                            float3 reflectedAlbedo = reflectedMaterial == 1 ? float3(0.85, 0.18, 0.16)
                                : reflectedMaterial == 2 ? float3(0.18, 0.35, 0.9)
                                : reflectedMaterial == 3 ? float3(0.9, 0.55, 0.3)
                                : float3(0.45, 0.48, 0.55);
                            reflection = reflectedAlbedo * (0.2 + 0.65 * saturate(dot(normalize(float3(0.2, 1.0, -0.35)), light)))
                                       + reflectedEmission * 0.8;
                            break;
                        }
                        reflectionTravel += max(0.02, reflectedDistance * 0.88);
                        if (reflectionTravel > 14.0) break;
                    }
                    float fresnel = pow(1.0 - saturate(dot(-rd, normal)), 5.0);

                    float gloss = material == 3 ? 0.58 : material == 2 ? 0.42 : 0.24;
                    float3 direct = albedo * (0.08 + 0.82 * diffuse);
                    float reflectionWeight = saturate(gloss + fresnel * 0.5);
                    color = lerp(direct, reflection, reflectionWeight);
                    color += float3(1.0) * pow(saturate(dot(reflected, light)), 96.0) * gloss * 1.4;
                    break;
                }

                travel += max(0.015, distance * 0.9);
                if (travel > 40.0) break;
            }

            color += glow;
            color = color / (1.0 + color * 0.6);
            return float4(pow(saturate(color), 0.85), 1.0);
        }
        """;

    /// <summary>Аддитивные частицы: проверка заливки и ALU.</summary>
    public const string Particles = """
        float4 PSParticles(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            int count = (int)lerp(40.0, 260.0, Quality);
            float3 color = 0.0;

            for (int index = 0; index < count; index++)
            {
                float f = (float)index;
                float t = Time * 0.8 + f * 0.137;
                float2 position = float2(sin(t * 1.13 + f * 0.7) * 1.1, cos(t * 0.91 + f * 1.7) * 0.62);
                float radius = 0.035 + 0.05 * frac(f * 0.37);
                float distance = length(uv - position);
                float glow = radius / (distance * distance + 0.00035);
                float3 tint = 0.5 + 0.5 * cos(f * 0.7 + float3(0.0, 2.1, 4.2));
                color += tint * glow;
            }

            color = color / (1.0 + color);
            return float4(color, 1.0);
        }
        """;

    /// <summary>Реймарчинг ландшафта с фрактальным шумом.</summary>
    public const string Terrain = """
        float TerrainHeight(float2 p)
        {
            int octaves = (int)lerp(4.0, 10.0, Quality);
            float h = Fbm(p * 0.6, octaves) * 2.4;
            h += 0.35 * Fbm(p * 3.1, max(2, octaves / 2));
            return h;
        }

        float3 TerrainNormal(float2 p, float epsilon)
        {
            float h = TerrainHeight(p);
            float hx = TerrainHeight(p + float2(epsilon, 0));
            float hz = TerrainHeight(p + float2(0, epsilon));
            return normalize(float3(h - hx, epsilon, h - hz));
        }

        float4 PSTerrain(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;

            float3 origin = float3(0.0, 3.2, Time * 1.6);
            float3 direction = normalize(float3(uv.x * 0.65, -0.45, 1.0));

            int steps = (int)lerp(60.0, 170.0, Quality);
            float travel = 0.05;
            float3 hit = float3(0, 0, 0);
            bool found = false;

            for (int step = 0; step < steps; step++)
            {
                float3 position = origin + direction * travel;
                float height = TerrainHeight(position.xz);
                if (position.y < height)
                {
                    hit = position;
                    found = true;
                    break;
                }

                travel += max(0.012, (position.y - height) * 0.45);
                if (travel > 60.0) break;
            }

            if (!found)
            {
                float3 sky = lerp(float3(0.05, 0.09, 0.18), float3(0.35, 0.5, 0.75), input.Uv.y);
                return float4(sky, 1.0);
            }

            float3 normal = TerrainNormal(hit.xz, 0.02);
            float3 light = normalize(float3(0.6, 0.8, -0.4));
            float diffuse = max(0.0, dot(normal, light));
            float3 base = lerp(float3(0.16, 0.22, 0.12), float3(0.55, 0.5, 0.4), diffuse);
            base += 0.15 * pow(max(0.0, dot(reflect(-light, normal), -direction)), 16.0);
            float fog = exp(-travel * 0.045);
            return float4(lerp(float3(0.1, 0.14, 0.22), base, fog), 1.0);
        }
        """;

    /// <summary>Итерации множества Жюлиа — очень тяжёлый пиксельный тест.</summary>
    public const string Julia = """
        float4 PSJulia(VSOut input) : SV_Target
        {
            float2 uv = input.Uv * 2.0 - 1.0;
            uv.x *= Resolution.x / Resolution.y;
            float2 z = uv * 1.35;
            float2 c = float2(-0.7269, 0.1889) + 0.08 * float2(sin(Time * 0.31), cos(Time * 0.27));

            int iterations = (int)lerp(120.0, 900.0, Quality);
            int used = 0;
            for (int i = 0; i < iterations; i++)
            {
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
                if (dot(z, z) > 256.0) break;
                used = i;
            }

            float ratio = (float)used / (float)iterations;
            float3 color = 0.5 + 0.5 * cos(3.0 + ratio * 14.0 + float3(0.0, 2.0, 4.0));
            return float4(color * (1.0 - ratio * 0.55), 1.0);
        }
        """;

    /// <summary>Размытие для многопроходной сцены (постобработка).</summary>
    public const string Blur = """
        Texture2D SourceTexture : register(t0);
        SamplerState SourceSampler : register(s0);

        float4 PSBlur(VSOut input) : SV_Target
        {
            float2 texel = 1.0 / Resolution;
            float radius = 1.0 + Quality * 3.0;
            float3 sum = 0.0;
            float weight = 0.0;

            for (int y = -4; y <= 4; y++)
            {
                for (int x = -4; x <= 4; x++)
                {
                    float2 offset = float2((float)x, (float)y) * texel * radius;
                    float w = 1.0 / (1.0 + dot(offset / texel, offset / texel) * 0.08);
                    sum += SourceTexture.Sample(SourceSampler, input.Uv + offset).rgb * w;
                    weight += w;
                }
            }

            return float4(sum / max(weight, 1e-5), 1.0);
        }
        """;
}
