shader_type spatial;
render_mode unshaded, wireframe, cull_disabled;

uniform vec4 line_color : source_color = vec4(0.03, 0.05, 0.07, 1.0);

void fragment()
{
    ALBEDO = line_color.rgb;
    ALPHA = line_color.a;
}
