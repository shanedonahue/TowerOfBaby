shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D grass_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D sand_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D sand_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform float texture_tiling_scale = 0.18;
uniform float blend_sharpness = 3.4;
uniform float grass_slope_max = 0.18;
uniform float rock_slope_start = 0.56;
uniform float sand_height_blend = 2.25;
uniform float water_level = -2.6;
uniform float terrain_roughness = 1.0;
uniform float vertex_tint_strength = 0.38;
uniform float normal_strength = 0.85;
uniform float normal_detail_max_distance = 72.0;
uniform float flat_normal_skip_slope = 0.12;
uniform float distant_mode_start = 140.0;
uniform vec4 material_tint : source_color = vec4(1.0);

varying vec3 world_position;
varying vec3 world_normal;

const int LAYER_GRASS = 0;
const int LAYER_DIRT = 1;
const int LAYER_ROCK = 2;
const int LAYER_SAND = 3;

float saturate(float value) {
    return clamp(value, 0.0, 1.0);
}

vec3 unpack_normal_map(vec3 encoded_normal) {
    return normalize(encoded_normal * 2.0 - 1.0);
}

float get_transition_width() {
    return 0.18 / max(blend_sharpness, 0.5);
}

vec2 get_x_projection_uv(vec3 position, float axis_sign) {
    return vec2(position.z * axis_sign, position.y) * texture_tiling_scale;
}

vec2 get_y_projection_uv(vec3 position, float axis_sign) {
    return vec2(position.x * axis_sign, position.z) * texture_tiling_scale;
}

vec2 get_z_projection_uv(vec3 position, float axis_sign) {
    return vec2(position.x * axis_sign, position.y) * texture_tiling_scale;
}

vec2 get_top_projection_uv(vec3 position, vec3 normal) {
    float sign_y = normal.y < 0.0 ? -1.0 : 1.0;
    return get_y_projection_uv(position, sign_y);
}

vec2 get_side_projection_uv(vec3 position, vec3 normal) {
    if (abs(normal.x) >= abs(normal.z)) {
        float sign_x = normal.x < 0.0 ? -1.0 : 1.0;
        return get_x_projection_uv(position, sign_x);
    }

    float sign_z = normal.z < 0.0 ? -1.0 : 1.0;
    return get_z_projection_uv(position, sign_z);
}

float get_top_projection_weight(vec3 normal) {
    float upness = abs(normal.y);
    return saturate(pow(upness, max((blend_sharpness * 0.65) + 0.65, 1.0)));
}

vec3 sample_reduced_triplanar_albedo(sampler2D albedo_texture, vec3 position, vec3 normal) {
    float top_weight = get_top_projection_weight(normal);
    if (top_weight >= 0.98) {
        return texture(albedo_texture, get_top_projection_uv(position, normal)).rgb;
    }

    if (top_weight <= 0.02) {
        return texture(albedo_texture, get_side_projection_uv(position, normal)).rgb;
    }

    vec3 side_color = texture(albedo_texture, get_side_projection_uv(position, normal)).rgb;
    vec3 top_color = texture(albedo_texture, get_top_projection_uv(position, normal)).rgb;
    return mix(side_color, top_color, top_weight);
}

vec3 sample_distant_albedo(sampler2D albedo_texture, vec3 position, vec3 normal) {
    float abs_x = abs(normal.x);
    float abs_y = abs(normal.y);
    float abs_z = abs(normal.z);
    if (abs_y >= abs_x && abs_y >= abs_z) {
        return texture(albedo_texture, get_top_projection_uv(position, normal)).rgb;
    }

    return texture(albedo_texture, get_side_projection_uv(position, normal)).rgb;
}

vec3 sample_top_world_normal(sampler2D normal_texture, vec3 position, vec3 normal) {
    float sign_y = normal.y < 0.0 ? -1.0 : 1.0;
    vec3 sampled = unpack_normal_map(texture(normal_texture, get_y_projection_uv(position, sign_y)).rgb);
    return normalize(vec3(sign_y * sampled.x, sign_y * sampled.z, sampled.y));
}

vec3 sample_side_world_normal(sampler2D normal_texture, vec3 position, vec3 normal) {
    if (abs(normal.x) >= abs(normal.z)) {
        float sign_x = normal.x < 0.0 ? -1.0 : 1.0;
        vec3 sampled_x = unpack_normal_map(texture(normal_texture, get_x_projection_uv(position, sign_x)).rgb);
        return normalize(vec3(sign_x * sampled_x.z, sampled_x.y, sign_x * sampled_x.x));
    }

    float sign_z = normal.z < 0.0 ? -1.0 : 1.0;
    vec3 sampled_z = unpack_normal_map(texture(normal_texture, get_z_projection_uv(position, sign_z)).rgb);
    return normalize(vec3(sign_z * sampled_z.x, sampled_z.y, sign_z * sampled_z.z));
}

vec3 sample_reduced_triplanar_normal(sampler2D normal_texture, vec3 position, vec3 normal) {
    float top_weight = get_top_projection_weight(normal);
    if (top_weight >= 0.98) {
        return sample_top_world_normal(normal_texture, position, normal);
    }

    if (top_weight <= 0.02) {
        return sample_side_world_normal(normal_texture, position, normal);
    }

    vec3 side_world_normal = sample_side_world_normal(normal_texture, position, normal);
    vec3 top_world_normal = sample_top_world_normal(normal_texture, position, normal);
    return normalize(mix(side_world_normal, top_world_normal, top_weight));
}

vec3 sample_layer_albedo(int layer_index, vec3 position, vec3 normal, bool distant_mode) {
    if (layer_index == LAYER_GRASS) {
        return distant_mode
            ? sample_distant_albedo(grass_albedo, position, normal)
            : sample_reduced_triplanar_albedo(grass_albedo, position, normal);
    }

    if (layer_index == LAYER_DIRT) {
        return distant_mode
            ? sample_distant_albedo(dirt_albedo, position, normal)
            : sample_reduced_triplanar_albedo(dirt_albedo, position, normal);
    }

    if (layer_index == LAYER_ROCK) {
        return distant_mode
            ? sample_distant_albedo(rock_albedo, position, normal)
            : sample_reduced_triplanar_albedo(rock_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(sand_albedo, position, normal)
        : sample_reduced_triplanar_albedo(sand_albedo, position, normal);
}

vec3 sample_layer_world_normal(int layer_index, vec3 position, vec3 normal) {
    if (layer_index == LAYER_GRASS) {
        return sample_reduced_triplanar_normal(grass_normal, position, normal);
    }

    if (layer_index == LAYER_DIRT) {
        return sample_reduced_triplanar_normal(dirt_normal, position, normal);
    }

    if (layer_index == LAYER_ROCK) {
        return sample_reduced_triplanar_normal(rock_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(sand_normal, position, normal);
}

float get_sand_weight(float slope, vec3 position) {
    float transition = get_transition_width();
    float sand_height_weight = 1.0 - smoothstep(0.0, sand_height_blend, abs(position.y - water_level));
    float sand_slope_weight = 1.0 - smoothstep(
        grass_slope_max + transition,
        max(rock_slope_start, grass_slope_max + transition + 0.02),
        slope);
    return sand_height_weight * sand_slope_weight;
}

void sample_surface_color(
    vec3 position,
    vec3 normal,
    float slope,
    bool distant_mode,
    out vec3 terrain_color,
    out int dominant_normal_layer)
{
    float transition = get_transition_width();
    float sand_weight = get_sand_weight(slope, position);
    vec3 base_color;
    int base_normal_layer;

    if (slope < grass_slope_max + transition) {
        float grass_to_dirt = smoothstep(
            max(grass_slope_max - transition, 0.0),
            grass_slope_max + transition,
            slope);
        vec3 grass_color = sample_layer_albedo(LAYER_GRASS, position, normal, distant_mode);
        if (grass_to_dirt > 0.02) {
            vec3 dirt_color = sample_layer_albedo(LAYER_DIRT, position, normal, distant_mode);
            base_color = mix(grass_color, dirt_color, grass_to_dirt);
        } else {
            base_color = grass_color;
        }

        base_normal_layer = grass_to_dirt > 0.55 ? LAYER_DIRT : LAYER_GRASS;
    } else if (slope > rock_slope_start - transition) {
        float dirt_to_rock = smoothstep(
            max(rock_slope_start - transition, 0.0),
            min(rock_slope_start + transition, 1.0),
            slope);
        vec3 dirt_color = sample_layer_albedo(LAYER_DIRT, position, normal, distant_mode);
        if (dirt_to_rock > 0.02) {
            vec3 rock_color = sample_layer_albedo(LAYER_ROCK, position, normal, distant_mode);
            base_color = mix(dirt_color, rock_color, dirt_to_rock);
        } else {
            base_color = dirt_color;
        }

        base_normal_layer = dirt_to_rock > 0.45 ? LAYER_ROCK : LAYER_DIRT;
    } else {
        base_color = sample_layer_albedo(LAYER_DIRT, position, normal, distant_mode);
        base_normal_layer = LAYER_DIRT;
    }

    if (sand_weight > 0.02) {
        vec3 sand_color = sample_layer_albedo(LAYER_SAND, position, normal, distant_mode);
        base_color = mix(base_color, sand_color, sand_weight);
        if (sand_weight > 0.5) {
            base_normal_layer = LAYER_SAND;
        }
    }

    terrain_color = base_color;
    dominant_normal_layer = base_normal_layer;
}

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

void fragment() {
    vec3 base_world_normal = normalize(world_normal);
    float slope = 1.0 - saturate(base_world_normal.y);
    float camera_distance = distance(CAMERA_POSITION_WORLD, world_position);
    bool distant_mode = camera_distance >= distant_mode_start;
    vec3 terrain_color;
    int dominant_normal_layer;
    sample_surface_color(
        world_position,
        base_world_normal,
        slope,
        distant_mode,
        terrain_color,
        dominant_normal_layer);

    bool use_detail_normals =
        !distant_mode &&
        normal_strength > 0.001 &&
        camera_distance < normal_detail_max_distance &&
        slope >= flat_normal_skip_slope;
    vec3 final_world_normal = base_world_normal;
    if (use_detail_normals) {
        vec3 sampled_world_normal = sample_layer_world_normal(
            dominant_normal_layer,
            world_position,
            base_world_normal);
        final_world_normal = normalize(mix(base_world_normal, sampled_world_normal, saturate(normal_strength)));
    }

    vec3 vertex_tint = mix(vec3(1.0), COLOR.rgb, saturate(vertex_tint_strength));

    ALBEDO = terrain_color * vertex_tint * material_tint.rgb;
    NORMAL = normalize((VIEW_MATRIX * vec4(final_world_normal, 0.0)).xyz);
    ROUGHNESS = terrain_roughness;
    SPECULAR = 0.22;
    METALLIC = 0.0;
}
