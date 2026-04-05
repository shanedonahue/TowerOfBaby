shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha;

uniform sampler2D blade_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform float wind_strength = 0.16;
uniform float wind_speed = 1.7;
uniform float wind_frequency = 0.11;
uniform float top_bend_power = 1.65;
uniform float roughness = 1.0;
uniform float alpha_scissor_threshold = 0.35;

varying float v_top_mask;

void vertex() {
    float top_mask = pow(clamp(1.0 - UV.y, 0.0, 1.0), top_bend_power);
    float phase = INSTANCE_CUSTOM.r * 6.28318530718;
    float sway_variation = mix(0.75, 1.25, INSTANCE_CUSTOM.g);
    vec3 world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float wave = sin(TIME * wind_speed + phase + world_vertex.x * wind_frequency + world_vertex.z * wind_frequency * 0.82);
    float gust = sin(TIME * (wind_speed * 0.43) + (phase * 1.71) + world_vertex.x * wind_frequency * 0.23 - world_vertex.z * wind_frequency * 0.31);
    vec2 sway_direction = normalize(vec2(cos(phase + (INSTANCE_CUSTOM.b * 6.28318530718)), sin(phase + (INSTANCE_CUSTOM.b * 6.28318530718))));
    vec2 sway = sway_direction * (wave + (gust * 0.45)) * wind_strength * sway_variation * top_mask;

    VERTEX.xz += sway;
    NORMAL = normalize(NORMAL + vec3(sway.x, 0.0, sway.y) * 0.6 * top_mask);
    v_top_mask = top_mask;
}

void fragment() {
    vec4 blade = texture(blade_texture, UV);
    ALBEDO = blade.rgb * COLOR.rgb;
    ALBEDO *= mix(0.76, 0.96, v_top_mask);
    ALPHA = blade.a * COLOR.a;
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
    ROUGHNESS = roughness;
    AO = 1.0;
}
