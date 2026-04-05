shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D grass_lush_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_lush_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_path_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_path_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_dry_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D grass_dry_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform sampler2D dirt_regular_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_regular_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_mud_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_mud_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_cracked_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D dirt_cracked_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform sampler2D rock_cliff_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_cliff_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_gravel_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_gravel_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_mossy_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D rock_mossy_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform sampler2D sand_shore_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D sand_shore_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D sand_coast_albedo : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D sand_coast_normal : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;

uniform float texture_tiling_scale = 0.14;
uniform float blend_sharpness = 2.6;
uniform float grass_slope_max = 0.30;
uniform float rock_slope_start = 0.44;
uniform float sand_height_blend = 3.2;
uniform float water_level = -2.6;
uniform float terrain_roughness = 1.0;
uniform float biome_variation_scale = 0.022;
uniform float vertex_tint_strength = 0.16;
uniform float normal_strength = 0.72;
uniform float normal_detail_max_distance = 72.0;
uniform float flat_normal_skip_slope = 0.12;
uniform float distant_mode_start = 140.0;
uniform vec4 material_tint : source_color = vec4(1.0);

varying vec3 world_position;
varying vec3 world_normal;
varying vec4 biome_weights_0;

const int BIOME_PLAINS = 0;
const int BIOME_ROCKY = 1;
const int BIOME_CANYON = 2;
const int BIOME_SWAMP = 3;
const int BIOME_VOLCANIC = 4;

const int SURFACE_GRASS = 0;
const int SURFACE_DIRT = 1;
const int SURFACE_ROCK = 2;
const int SURFACE_SAND = 3;

const int GRASS_LUSH = 0;
const int GRASS_PATH = 1;
const int GRASS_DRY = 2;

const int DIRT_REGULAR = 0;
const int DIRT_MUD = 1;
const int DIRT_CRACKED = 2;

const int ROCK_CLIFF = 0;
const int ROCK_GRAVEL = 1;
const int ROCK_MOSSY = 2;

const int SAND_SHORE = 0;
const int SAND_COAST = 1;

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

float get_category_variation(vec3 position, float offset) {
    vec2 sample_position = (position.xz * biome_variation_scale) + vec2(offset * 1.173, offset * 0.619);
    float macro_a = sin(sample_position.x + (sample_position.y * 0.83));
    float macro_b = cos((sample_position.x * 1.37) - (sample_position.y * 1.11));
    return saturate((macro_a + macro_b) * 0.25 + 0.5);
}

void get_biome_weights(
    out float plains_weight,
    out float rocky_weight,
    out float canyon_weight,
    out float swamp_weight,
    out float volcanic_weight)
{
    plains_weight = max(biome_weights_0.x, 0.0);
    rocky_weight = max(biome_weights_0.y, 0.0);
    canyon_weight = max(biome_weights_0.z, 0.0);
    swamp_weight = max(biome_weights_0.w, 0.0);
    float non_volcanic_sum = plains_weight + rocky_weight + canyon_weight + swamp_weight;
    if (non_volcanic_sum < 0.001) {
        plains_weight = 1.0;
        rocky_weight = 0.0;
        canyon_weight = 0.0;
        swamp_weight = 0.0;
        volcanic_weight = 0.0;
        return;
    }

    volcanic_weight = max(0.0, 1.0 - non_volcanic_sum);
}

int get_dominant_biome() {
    float plains_weight;
    float rocky_weight;
    float canyon_weight;
    float swamp_weight;
    float volcanic_weight;
    get_biome_weights(
        plains_weight,
        rocky_weight,
        canyon_weight,
        swamp_weight,
        volcanic_weight);

    int dominant_biome = BIOME_PLAINS;
    float strongest_weight = plains_weight;

    if (rocky_weight > strongest_weight) {
        dominant_biome = BIOME_ROCKY;
        strongest_weight = rocky_weight;
    }

    if (canyon_weight > strongest_weight) {
        dominant_biome = BIOME_CANYON;
        strongest_weight = canyon_weight;
    }

    if (swamp_weight > strongest_weight) {
        dominant_biome = BIOME_SWAMP;
        strongest_weight = swamp_weight;
    }

    if (volcanic_weight > strongest_weight) {
        dominant_biome = BIOME_VOLCANIC;
    }

    return dominant_biome;
}

void get_grass_variants(int dominant_biome, out int variant_a, out int variant_b) {
    if (dominant_biome == BIOME_ROCKY) {
        variant_a = GRASS_PATH;
        variant_b = GRASS_DRY;
        return;
    }

    if (dominant_biome == BIOME_CANYON) {
        variant_a = GRASS_DRY;
        variant_b = GRASS_PATH;
        return;
    }

    if (dominant_biome == BIOME_SWAMP) {
        variant_a = GRASS_LUSH;
        variant_b = GRASS_PATH;
        return;
    }

    if (dominant_biome == BIOME_VOLCANIC) {
        variant_a = GRASS_DRY;
        variant_b = GRASS_PATH;
        return;
    }

    variant_a = GRASS_LUSH;
    variant_b = GRASS_PATH;
}

void get_dirt_variants(int dominant_biome, out int variant_a, out int variant_b) {
    if (dominant_biome == BIOME_ROCKY) {
        variant_a = DIRT_REGULAR;
        variant_b = DIRT_REGULAR;
        return;
    }

    if (dominant_biome == BIOME_CANYON) {
        variant_a = DIRT_CRACKED;
        variant_b = DIRT_REGULAR;
        return;
    }

    if (dominant_biome == BIOME_SWAMP) {
        variant_a = DIRT_MUD;
        variant_b = DIRT_REGULAR;
        return;
    }

    if (dominant_biome == BIOME_VOLCANIC) {
        variant_a = DIRT_CRACKED;
        variant_b = DIRT_REGULAR;
        return;
    }

    variant_a = DIRT_REGULAR;
    variant_b = DIRT_REGULAR;
}

void get_rock_variants(int dominant_biome, out int variant_a, out int variant_b) {
    if (dominant_biome == BIOME_ROCKY) {
        variant_a = ROCK_GRAVEL;
        variant_b = ROCK_CLIFF;
        return;
    }

    if (dominant_biome == BIOME_CANYON) {
        variant_a = ROCK_GRAVEL;
        variant_b = ROCK_CLIFF;
        return;
    }

    if (dominant_biome == BIOME_SWAMP) {
        variant_a = ROCK_GRAVEL;
        variant_b = ROCK_MOSSY;
        return;
    }

    if (dominant_biome == BIOME_VOLCANIC) {
        variant_a = ROCK_GRAVEL;
        variant_b = ROCK_CLIFF;
        return;
    }

    variant_a = ROCK_GRAVEL;
    variant_b = ROCK_MOSSY;
}

void get_sand_variants(int dominant_biome, out int variant_a, out int variant_b) {
    if (dominant_biome == BIOME_ROCKY || dominant_biome == BIOME_CANYON || dominant_biome == BIOME_VOLCANIC) {
        variant_a = SAND_COAST;
        variant_b = SAND_SHORE;
        return;
    }

    variant_a = SAND_SHORE;
    variant_b = SAND_COAST;
}

vec3 sample_grass_albedo_variant(int variant_index, vec3 position, vec3 normal, bool distant_mode) {
    if (variant_index == GRASS_PATH) {
        return distant_mode
            ? sample_distant_albedo(grass_path_albedo, position, normal)
            : sample_reduced_triplanar_albedo(grass_path_albedo, position, normal);
    }

    if (variant_index == GRASS_DRY) {
        return distant_mode
            ? sample_distant_albedo(grass_dry_albedo, position, normal)
            : sample_reduced_triplanar_albedo(grass_dry_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(grass_lush_albedo, position, normal)
        : sample_reduced_triplanar_albedo(grass_lush_albedo, position, normal);
}

vec3 sample_dirt_albedo_variant(int variant_index, vec3 position, vec3 normal, bool distant_mode) {
    if (variant_index == DIRT_MUD) {
        return distant_mode
            ? sample_distant_albedo(dirt_mud_albedo, position, normal)
            : sample_reduced_triplanar_albedo(dirt_mud_albedo, position, normal);
    }

    if (variant_index == DIRT_CRACKED) {
        return distant_mode
            ? sample_distant_albedo(dirt_cracked_albedo, position, normal)
            : sample_reduced_triplanar_albedo(dirt_cracked_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(dirt_regular_albedo, position, normal)
        : sample_reduced_triplanar_albedo(dirt_regular_albedo, position, normal);
}

vec3 sample_rock_albedo_variant(int variant_index, vec3 position, vec3 normal, bool distant_mode) {
    if (variant_index == ROCK_GRAVEL) {
        return distant_mode
            ? sample_distant_albedo(rock_gravel_albedo, position, normal)
            : sample_reduced_triplanar_albedo(rock_gravel_albedo, position, normal);
    }

    if (variant_index == ROCK_MOSSY) {
        return distant_mode
            ? sample_distant_albedo(rock_mossy_albedo, position, normal)
            : sample_reduced_triplanar_albedo(rock_mossy_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(rock_cliff_albedo, position, normal)
        : sample_reduced_triplanar_albedo(rock_cliff_albedo, position, normal);
}

vec3 sample_sand_albedo_variant(int variant_index, vec3 position, vec3 normal, bool distant_mode) {
    if (variant_index == SAND_COAST) {
        return distant_mode
            ? sample_distant_albedo(sand_coast_albedo, position, normal)
            : sample_reduced_triplanar_albedo(sand_coast_albedo, position, normal);
    }

    return distant_mode
        ? sample_distant_albedo(sand_shore_albedo, position, normal)
        : sample_reduced_triplanar_albedo(sand_shore_albedo, position, normal);
}

vec3 sample_grass_normal_variant(int variant_index, vec3 position, vec3 normal) {
    if (variant_index == GRASS_PATH) {
        return sample_reduced_triplanar_normal(grass_path_normal, position, normal);
    }

    if (variant_index == GRASS_DRY) {
        return sample_reduced_triplanar_normal(grass_dry_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(grass_lush_normal, position, normal);
}

vec3 sample_dirt_normal_variant(int variant_index, vec3 position, vec3 normal) {
    if (variant_index == DIRT_MUD) {
        return sample_reduced_triplanar_normal(dirt_mud_normal, position, normal);
    }

    if (variant_index == DIRT_CRACKED) {
        return sample_reduced_triplanar_normal(dirt_cracked_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(dirt_regular_normal, position, normal);
}

vec3 sample_rock_normal_variant(int variant_index, vec3 position, vec3 normal) {
    if (variant_index == ROCK_GRAVEL) {
        return sample_reduced_triplanar_normal(rock_gravel_normal, position, normal);
    }

    if (variant_index == ROCK_MOSSY) {
        return sample_reduced_triplanar_normal(rock_mossy_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(rock_cliff_normal, position, normal);
}

vec3 sample_sand_normal_variant(int variant_index, vec3 position, vec3 normal) {
    if (variant_index == SAND_COAST) {
        return sample_reduced_triplanar_normal(sand_coast_normal, position, normal);
    }

    return sample_reduced_triplanar_normal(sand_shore_normal, position, normal);
}

vec3 sample_surface_albedo(
    int surface_type,
    int dominant_biome,
    vec3 position,
    vec3 normal,
    bool distant_mode,
    out int chosen_normal_variant)
{
    float variation = get_category_variation(position, float(surface_type) * 17.0 + float(dominant_biome) * 5.0);
    float variation_blend = smoothstep(0.28, 0.72, variation);
    int variant_a = 0;
    int variant_b = 0;

    if (surface_type == SURFACE_GRASS) {
        get_grass_variants(dominant_biome, variant_a, variant_b);
        if (dominant_biome == BIOME_PLAINS && variant_a == GRASS_LUSH && variant_b == GRASS_PATH) {
            variation_blend = smoothstep(0.74, 0.92, variation);
        }
        chosen_normal_variant = variation_blend > 0.5 ? variant_b : variant_a;
        if (distant_mode) {
            return sample_grass_albedo_variant(chosen_normal_variant, position, normal, true);
        }

        vec3 color_a = sample_grass_albedo_variant(variant_a, position, normal, false);
        if (variation_blend <= 0.02) {
            return color_a;
        }

        vec3 color_b = sample_grass_albedo_variant(variant_b, position, normal, false);
        if (variation_blend >= 0.98) {
            return color_b;
        }

        return mix(color_a, color_b, variation_blend);
    }

    if (surface_type == SURFACE_DIRT) {
        get_dirt_variants(dominant_biome, variant_a, variant_b);
        chosen_normal_variant = variation_blend > 0.5 ? variant_b : variant_a;
        if (distant_mode) {
            return sample_dirt_albedo_variant(chosen_normal_variant, position, normal, true);
        }

        vec3 color_a = sample_dirt_albedo_variant(variant_a, position, normal, false);
        if (variation_blend <= 0.02) {
            return color_a;
        }

        vec3 color_b = sample_dirt_albedo_variant(variant_b, position, normal, false);
        if (variation_blend >= 0.98) {
            return color_b;
        }

        return mix(color_a, color_b, variation_blend);
    }

    if (surface_type == SURFACE_ROCK) {
        get_rock_variants(dominant_biome, variant_a, variant_b);
        chosen_normal_variant = variation_blend > 0.5 ? variant_b : variant_a;
        if (distant_mode) {
            return sample_rock_albedo_variant(chosen_normal_variant, position, normal, true);
        }

        vec3 color_a = sample_rock_albedo_variant(variant_a, position, normal, false);
        if (variation_blend <= 0.02) {
            return color_a;
        }

        vec3 color_b = sample_rock_albedo_variant(variant_b, position, normal, false);
        if (variation_blend >= 0.98) {
            return color_b;
        }

        return mix(color_a, color_b, variation_blend);
    }

    get_sand_variants(dominant_biome, variant_a, variant_b);
    chosen_normal_variant = variation_blend > 0.5 ? variant_b : variant_a;
    if (distant_mode) {
        return sample_sand_albedo_variant(chosen_normal_variant, position, normal, true);
    }

    vec3 color_a = sample_sand_albedo_variant(variant_a, position, normal, false);
    if (variation_blend <= 0.02) {
        return color_a;
    }

    vec3 color_b = sample_sand_albedo_variant(variant_b, position, normal, false);
    if (variation_blend >= 0.98) {
        return color_b;
    }

    return mix(color_a, color_b, variation_blend);
}

vec3 sample_surface_normal(
    int surface_type,
    int variant_index,
    vec3 position,
    vec3 normal)
{
    if (surface_type == SURFACE_GRASS) {
        return sample_grass_normal_variant(variant_index, position, normal);
    }

    if (surface_type == SURFACE_DIRT) {
        return sample_dirt_normal_variant(variant_index, position, normal);
    }

    if (surface_type == SURFACE_ROCK) {
        return sample_rock_normal_variant(variant_index, position, normal);
    }

    return sample_sand_normal_variant(variant_index, position, normal);
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
    int dominant_biome,
    out vec3 terrain_color,
    out int dominant_normal_layer,
    out int dominant_normal_variant)
{
    float transition = get_transition_width();
    float sand_weight = get_sand_weight(slope, position);
    vec3 base_color;
    int base_normal_layer;
    int base_normal_variant;

    if (slope < grass_slope_max + transition) {
        float grass_to_dirt = smoothstep(
            max(grass_slope_max - transition, 0.0),
            grass_slope_max + transition,
            slope);
        int grass_variant;
        vec3 grass_color = sample_surface_albedo(
            SURFACE_GRASS,
            dominant_biome,
            position,
            normal,
            distant_mode,
            grass_variant);
        if (grass_to_dirt > 0.02) {
            int dirt_variant;
            vec3 dirt_color = sample_surface_albedo(
                SURFACE_DIRT,
                dominant_biome,
                position,
                normal,
                distant_mode,
                dirt_variant);
            base_color = mix(grass_color, dirt_color, grass_to_dirt);
            if (grass_to_dirt > 0.55) {
                base_normal_layer = SURFACE_DIRT;
                base_normal_variant = dirt_variant;
            } else {
                base_normal_layer = SURFACE_GRASS;
                base_normal_variant = grass_variant;
            }
        } else {
            base_color = grass_color;
            base_normal_layer = SURFACE_GRASS;
            base_normal_variant = grass_variant;
        }
    } else if (slope > rock_slope_start - transition) {
        float dirt_to_rock = smoothstep(
            max(rock_slope_start - transition, 0.0),
            min(rock_slope_start + transition, 1.0),
            slope);
        int dirt_variant;
        vec3 dirt_color = sample_surface_albedo(
            SURFACE_DIRT,
            dominant_biome,
            position,
            normal,
            distant_mode,
            dirt_variant);
        if (dirt_to_rock > 0.02) {
            int rock_variant;
            vec3 rock_color = sample_surface_albedo(
                SURFACE_ROCK,
                dominant_biome,
                position,
                normal,
                distant_mode,
                rock_variant);
            base_color = mix(dirt_color, rock_color, dirt_to_rock);
            if (dirt_to_rock > 0.45) {
                base_normal_layer = SURFACE_ROCK;
                base_normal_variant = rock_variant;
            } else {
                base_normal_layer = SURFACE_DIRT;
                base_normal_variant = dirt_variant;
            }
        } else {
            base_color = dirt_color;
            base_normal_layer = SURFACE_DIRT;
            base_normal_variant = dirt_variant;
        }
    } else {
        int dirt_variant;
        base_color = sample_surface_albedo(
            SURFACE_DIRT,
            dominant_biome,
            position,
            normal,
            distant_mode,
            dirt_variant);
        base_normal_layer = SURFACE_DIRT;
        base_normal_variant = dirt_variant;
    }

    if (sand_weight > 0.02) {
        int sand_variant;
        vec3 sand_color = sample_surface_albedo(
            SURFACE_SAND,
            dominant_biome,
            position,
            normal,
            distant_mode,
            sand_variant);
        base_color = mix(base_color, sand_color, sand_weight);
        if (sand_weight > 0.5) {
            base_normal_layer = SURFACE_SAND;
            base_normal_variant = sand_variant;
        }
    }

    terrain_color = base_color;
    dominant_normal_layer = base_normal_layer;
    dominant_normal_variant = base_normal_variant;
}

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
    biome_weights_0 = CUSTOM0;
}

void fragment() {
    vec3 base_world_normal = normalize(world_normal);
    float slope = 1.0 - saturate(base_world_normal.y);
    float camera_distance = distance(CAMERA_POSITION_WORLD, world_position);
    bool distant_mode = camera_distance >= distant_mode_start;
    int dominant_biome = get_dominant_biome();
    vec3 terrain_color;
    int dominant_normal_layer;
    int dominant_normal_variant;
    sample_surface_color(
        world_position,
        base_world_normal,
        slope,
        distant_mode,
        dominant_biome,
        terrain_color,
        dominant_normal_layer,
        dominant_normal_variant);

    bool use_detail_normals =
        !distant_mode &&
        normal_strength > 0.001 &&
        camera_distance < normal_detail_max_distance &&
        slope >= flat_normal_skip_slope;
    vec3 final_world_normal = base_world_normal;
    if (use_detail_normals) {
        vec3 sampled_world_normal = sample_surface_normal(
            dominant_normal_layer,
            dominant_normal_variant,
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
