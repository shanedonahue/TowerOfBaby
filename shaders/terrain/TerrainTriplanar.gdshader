shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D grass_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform float texture_tiling_scale = 0.32;
uniform float blend_sharpness = 3.0;
uniform float grass_slope_max = 0.28;
uniform float rock_slope_start = 0.42;
uniform float water_level = -3.4;
uniform float terrain_roughness = 1.0;
uniform float vertex_tint_strength = 0.06;
uniform float normal_strength = 0.75;
uniform float normal_detail_max_distance = 72.0;
uniform float flat_normal_skip_slope = 0.12;
uniform float distant_mode_start = 140.0;
uniform float ambient_fill = 0.07;
uniform float ambient_steep_boost = 0.05;
uniform float diffuse_wrap = 0.30;
uniform float diffuse_floor = 0.28;
uniform vec4 material_tint : source_color = vec4(1.0);

varying vec3 world_position;
varying vec3 world_normal;

const int SURFACE_GRASS = 0;
const int SURFACE_DIRT = 1;
const int SURFACE_ROCK = 2;
const int MATERIAL_SOIL = 0;
const int MATERIAL_GRASS = 1;
const int MATERIAL_ROCK = 2;
const int MATERIAL_CLIFF = 3;
const int MATERIAL_SNOW = 4;
const int MATERIAL_SCORCHED = 5;
const float MATERIAL_DECODE_SCALE = 8.0;

float saturate(float value) {
    return clamp(value, 0.0, 1.0);
}

vec3 unpack_normal_map(vec3 encoded_normal) {
    return normalize(encoded_normal * 2.0 - 1.0);
}

float get_transition_width() {
    return 0.16 / max(blend_sharpness, 0.5);
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

vec3 sample_surface_albedo(
    int surface_type,
    vec3 position,
    vec3 normal,
    bool distant_mode)
{
    if (surface_type == SURFACE_GRASS) {
        return distant_mode
            ? sample_distant_albedo(grass_albedo, position, normal)
            : sample_reduced_triplanar_albedo(grass_albedo, position, normal);
    }

    if (surface_type == SURFACE_DIRT) {
        return distant_mode
            ? sample_distant_albedo(dirt_albedo, position, normal)
            : sample_reduced_triplanar_albedo(dirt_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(rock_albedo, position, normal)
        : sample_reduced_triplanar_albedo(rock_albedo, position, normal);
}

vec3 sample_surface_normal(
    int surface_type,
    vec3 position,
    vec3 normal)
{
    if (surface_type == SURFACE_GRASS) {
        return sample_reduced_triplanar_normal(grass_normal, position, normal);
    }

    if (surface_type == SURFACE_DIRT) {
        return sample_reduced_triplanar_normal(dirt_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(rock_normal, position, normal);
}

int decode_material_id(float encoded_alpha) {
    if (encoded_alpha >= 0.90) {
        return MATERIAL_SOIL;
    }

    return int(clamp(floor(encoded_alpha * MATERIAL_DECODE_SCALE), 0.0, 5.0));
}

int resolve_primary_surface(int material_id) {
    if (material_id == MATERIAL_GRASS) {
        return SURFACE_GRASS;
    }

    if (material_id == MATERIAL_SOIL) {
        return SURFACE_DIRT;
    }

    return SURFACE_ROCK;
}

int resolve_dominant_normal_layer(int material_id) {
    if (material_id == MATERIAL_GRASS) {
        return SURFACE_GRASS;
    }

    if (material_id == MATERIAL_SOIL) {
        return SURFACE_DIRT;
    }

    return SURFACE_ROCK;
}

float resolve_secondary_surface_weight(int material_id, float slope) {
    if (material_id != MATERIAL_GRASS) {
        return 0.0;
    }

    return smoothstep(
        max(grass_slope_max * 0.90, 0.18),
        max(grass_slope_max + 0.26, 0.48),
        slope) * 0.20;
}

void sample_surface_color(
    vec3 position,
    vec3 normal,
    int material_id,
    float slope,
    bool distant_mode,
    out vec3 terrain_color,
    out int dominant_normal_layer)
{
    int primary_surface = resolve_primary_surface(material_id);
    dominant_normal_layer = resolve_dominant_normal_layer(material_id);
    vec3 primary_color = sample_surface_albedo(primary_surface, position, normal, distant_mode);

    float secondary_weight = resolve_secondary_surface_weight(material_id, slope);
    if (secondary_weight <= 0.001) {
        terrain_color = primary_color;
        return;
    }

    vec3 secondary_color = sample_surface_albedo(SURFACE_DIRT, position, normal, distant_mode);
    terrain_color = mix(primary_color, secondary_color, saturate(secondary_weight));
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
    int material_id = decode_material_id(COLOR.a);
    vec3 terrain_color;
    int dominant_normal_layer;
    sample_surface_color(
        world_position,
        base_world_normal,
        material_id,
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
        vec3 sampled_world_normal = sample_surface_normal(
            dominant_normal_layer,
            world_position,
            base_world_normal);
        final_world_normal = normalize(mix(base_world_normal, sampled_world_normal, saturate(normal_strength)));
    }

    vec3 vertex_tint = mix(vec3(1.0), COLOR.rgb, saturate(vertex_tint_strength));

    ALBEDO = terrain_color * vertex_tint * material_tint.rgb;
    float steep_fill = 1.0 - abs(base_world_normal.y);
    EMISSION = ALBEDO * (ambient_fill + (steep_fill * ambient_steep_boost));
    NORMAL = normalize((VIEW_MATRIX * vec4(final_world_normal, 0.0)).xyz);
    ROUGHNESS = terrain_roughness;
    SPECULAR = 0.14;
    METALLIC = 0.0;
}

void light() {
    float ndotl = saturate(dot(NORMAL, LIGHT));
    float wrapped_diffuse = saturate((ndotl + diffuse_wrap) / (1.0 + diffuse_wrap));
    float softened = mix(ndotl, wrapped_diffuse, 0.70);
    float remapped = mix(diffuse_floor, 1.0, softened);
    DIFFUSE_LIGHT += ATTENUATION * LIGHT_COLOR * ALBEDO * remapped;
}
