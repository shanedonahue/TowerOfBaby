shader_type spatial;
render_mode blend_mix, depth_prepass_alpha, cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;

uniform vec4 shallow_color : source_color = vec4(0.11, 0.37, 0.48, 1.0);
uniform vec4 deep_color : source_color = vec4(0.02, 0.11, 0.21, 1.0);
uniform vec4 shore_color : source_color = vec4(0.20, 0.51, 0.52, 1.0);
uniform vec4 fresnel_color : source_color = vec4(0.30, 0.49, 0.63, 1.0);

uniform float shallow_depth = 1.6;
uniform float deep_depth = 13.0;
uniform float shore_fade_depth = 0.8;
uniform float alpha_shallow = 0.60;
uniform float alpha_deep = 0.88;
uniform float fresnel_strength = 0.12;
uniform float fresnel_power = 4.8;
uniform float specular_strength = 0.22;
uniform float metallic_amount = 0.0;
uniform float roughness_near = 0.24;
uniform float roughness_far = 0.14;
uniform float shore_glow = 0.03;
uniform float macro_tint_strength = 0.05;

varying vec3 world_position;
varying float water_view_depth;

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    water_view_depth = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
}

void fragment() {
    float raw_depth = textureLod(depth_texture, SCREEN_UV, 0.0).r;
    float surface_depth = max(water_view_depth, 0.0001);
    vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw_depth);
    vec4 view_position = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
    view_position.xyz /= max(view_position.w, 0.0001);
    float linear_depth = -view_position.z;
    float scene_depth = raw_depth >= 0.9999
        ? surface_depth + deep_depth
        : linear_depth;
    float water_depth = clamp(scene_depth - surface_depth, 0.0, deep_depth * 1.5);

    float deep_factor = smoothstep(shallow_depth * 0.75, deep_depth, water_depth);
    float shore_factor = 1.0 - smoothstep(0.0, shore_fade_depth, water_depth);

    float macro_a = sin(world_position.x * 0.010 + world_position.z * 0.013);
    float macro_b = cos(world_position.x * 0.017 - world_position.z * 0.011);
    float macro_mix = (macro_a + macro_b) * 0.25 + 0.5;

    vec3 base_color = mix(shallow_color.rgb, deep_color.rgb, deep_factor);
    base_color = mix(base_color, shore_color.rgb, shore_factor * 0.70);
    base_color *= mix(1.0 - macro_tint_strength, 1.0 + macro_tint_strength, macro_mix);

    float fresnel = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), fresnel_power);
    vec3 final_color = mix(base_color, fresnel_color.rgb, fresnel * fresnel_strength);
    final_color *= mix(0.94, 1.02, macro_mix);

    ALBEDO = final_color;
    METALLIC = metallic_amount;
    SPECULAR = specular_strength;
    ROUGHNESS = mix(roughness_near, roughness_far, deep_factor);
    RIM = fresnel * 0.08;
    RIM_TINT = 0.60;
    EMISSION = shore_color.rgb * shore_factor * shore_glow;
    ALPHA = mix(alpha_shallow, alpha_deep, deep_factor);
}
