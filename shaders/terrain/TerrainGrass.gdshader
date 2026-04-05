shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha, diffuse_lambert_wrap, specular_disabled;

uniform sampler2D blade_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform float atlas_columns = 4.0;
uniform float atlas_rows = 1.0;
uniform float atlas_frame_count = 4.0;
uniform float atlas_padding = 0.004;
uniform float wind_strength = 0.16;
uniform float wind_speed = 1.7;
uniform float wind_frequency = 0.11;
uniform float top_bend_power = 1.65;
uniform float roughness = 0.96;
uniform float alpha_scissor_threshold = 0.18;

varying float v_top_mask;
varying vec2 v_blade_uv;

void vertex() {
    float blade_height = clamp(1.0 - UV.y, 0.0, 1.0);
    float top_mask = pow(blade_height, top_bend_power);
    float phase = INSTANCE_CUSTOM.r * 6.28318530718;
    float sway_variation = mix(0.75, 1.25, INSTANCE_CUSTOM.g);
    vec3 world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float wave = sin(TIME * wind_speed + phase + world_vertex.x * wind_frequency + world_vertex.z * wind_frequency * 0.82);
    float gust = sin(TIME * (wind_speed * 0.46) + (phase * 1.71) + world_vertex.x * wind_frequency * 0.23 - world_vertex.z * wind_frequency * 0.31);
    float flutter = sin(TIME * (wind_speed * 2.1) + (phase * 3.37) + world_vertex.x * 0.19 + world_vertex.z * 0.13 + blade_height * 8.0);
    vec2 sway_direction = normalize(vec2(cos(phase + (INSTANCE_CUSTOM.b * 6.28318530718)), sin(phase + (INSTANCE_CUSTOM.b * 6.28318530718))));
    vec2 sway = sway_direction * ((wave * 0.82) + (gust * 0.55) + (flutter * 0.10)) * wind_strength * sway_variation * top_mask;

    VERTEX.xz += sway;
    VERTEX.y += abs(wave + (gust * 0.35)) * wind_strength * 0.045 * top_mask;
    vec3 bent_normal = normalize(NORMAL + vec3(sway.x, 0.08 * top_mask, sway.y) * 0.28);
    NORMAL = normalize(mix(bent_normal, vec3(0.0, 1.0, 0.0), 0.52));
    v_top_mask = blade_height;

    float columns = max(1.0, atlas_columns);
    float rows = max(1.0, atlas_rows);
    float frame_count = max(1.0, atlas_frame_count);
    float frame_index = min(frame_count - 1.0, floor(clamp(INSTANCE_CUSTOM.a, 0.0, 0.9999) * frame_count));
    float column_index = mod(frame_index, columns);
    float row_index = floor(frame_index / columns);
    float frame_start_x = column_index / columns;
    float frame_end_x = (column_index + 1.0) / columns;
    float frame_start_y = row_index / rows;
    float frame_end_y = (row_index + 1.0) / rows;
    v_blade_uv = vec2(
        mix(frame_start_x + atlas_padding, frame_end_x - atlas_padding, UV.x),
        mix(frame_start_y + atlas_padding, frame_end_y - atlas_padding, UV.y)
    );
}

void fragment() {
    vec4 blade = texture(blade_texture, v_blade_uv);
    float blade_alpha = blade.a;
    vec3 blade_rgb = blade.rgb;
    float body_mask = smoothstep(alpha_scissor_threshold * 0.55, 1.0, blade_alpha);
    float edge_mask = smoothstep(0.02, 0.82, blade_alpha);
    float facing = FRONT_FACING ? 1.0 : 0.0;
    vec3 foliage_normal = normalize(FRONT_FACING ? NORMAL : -NORMAL);
    NORMAL = foliage_normal;
    vec3 base_tint = COLOR.rgb * 0.90;
    vec3 tip_tint = COLOR.rgb * 1.02;
    vec3 tint = mix(base_tint, tip_tint, pow(v_top_mask, 0.78));
    ALBEDO = blade_rgb * tint;
    ALBEDO *= mix(0.98, 1.02, (edge_mask * 0.22) + (pow(v_top_mask, 0.72) * 0.14));
    ALPHA = blade_alpha * COLOR.a;
    ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
    ROUGHNESS = mix(roughness + 0.02, roughness - 0.03, body_mask * 0.38);
    AO = mix(0.96, 1.0, pow(v_top_mask, 0.7));
    BACKLIGHT = ALBEDO * mix(0.12, 0.04, facing) * body_mask;
}
