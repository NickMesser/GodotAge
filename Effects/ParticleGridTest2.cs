#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Effects;

/// <summary>Editable 4x4 fidelity gallery. Change <see cref="Samples"/> to compare another set.</summary>
public partial class ParticleGridTest2 : Node3D
{
    private const int GridColumns = 4;
    private const float CellWidth = 9f;
    private const float CellHeight = 7f;
    private const float GridTop = 11.5f;
    private const float LabelYOffset = -2.5f;
    private const float OneShotRepeatSeconds = 2f;
    private const float CameraOrthogonalSize = 30f;
    private static readonly Vector3 CameraPosition = new(0,1,55);
    private static readonly Vector3 CameraTarget = new(0,1,0);

    public sealed record Sample(string Label,string Effect,string Library,bool Moving=false);
    public static Sample[] Samples =
    [
        new("Fire projectile","element.el_fire0001_proj","game/libs/particles/ability_skill_table.xml"),
        new("Fireball","s_effects.s0029","game/libs/particles/pc_skill.xml"),
        new("Portal glow","system_skill.mini_portal","game/libs/particles/abillity_skill_table_m.xml"),
        new("Black smoke","black_smoke.a","game/libs/particles/smoke_and_fire.xml"),
        new("Sword trail","sword_trail_test1","game/libs/particles/x2_effects.xml",true),
        new("Healing","p_skill_love.note_heal_16_hit","game/libs/particles/ability_skill_table_k.xml"),
        new("Wind buff","element.el_wind0002_buff","game/libs/particles/ability_skill_table.xml"),
        new("Chain lightning","magic.chain_lightning_proj","game/libs/particles/abillity_skill_table_m.xml"),
        new("Hit spark","sparks.electric","game/libs/particles/misc.xml"),
        new("Fire hit","element.el_fire0001_hit","game/libs/particles/ability_skill_table.xml"),
        new("Fire burst","element.el_fire0002_form01","game/libs/particles/ability_skill_table.xml"),
        new("Projectile trail","p_skill_vocation.shot_sword_proj","game/libs/particles/ability_skill_table_k.xml"),
        new("Totem aura","397.Fx_Ancient_Totem_Aura_01","game/libs/particles/fx_environment.xml"),
        new("Lightning sparks","sparks.electric_blue","game/libs/particles/misc.xml"),
        new("Ice shatter","Shatter.1","game/libs/particles/ice.xml"),
        new("Brazier fire","397.Brazier_Fire","game/libs/particles/fx_environment.xml")
    ];
    private readonly Node3D?[] _cells=new Node3D?[Samples.Length];
    private readonly bool[] _repeatOneShot=new bool[Samples.Length];
    private readonly EffectPlayer?[] _playbacks=new EffectPlayer?[Samples.Length];
    private Node3D? _moving; private Vector3 _movingCenter; private EffectPlayer? _player; private float _time,_repeat;

    public override async void _Ready()
    {
        AddWorld();AddCamera();var args=Args(OS.GetCmdlineUserArgs());var pak=ClientPaths.Pak;
        if(!PakFiles.Open(pak)){GD.PrintErr($"Could not open game pak: {pak}");GetTree().Quit(1);return;}
        var player=new EffectPlayer{Name="ParticleGridPlayer"};_player=player;AddChild(player);var paths=Samples.Select(x=>x.Library).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();GD.Print($"Loaded {player.LoadLibraries(paths)}/{paths.Length} particle libraries.");
        for(var i=0;i<Samples.Length;i++)
        {
            var s=Samples[i];var pos=Cell(i);var cell=new Node3D{Name=$"Cell {i} {s.Label}",Position=pos};AddChild(cell);_cells[i]=cell;
            var authoredContinuous=player.IsAuthoredContinuous(s.Effect);_repeatOneShot[i]=!s.Moving&&player.IsAuthoredOneShot(s.Effect);
            if(player.Factory.DebugEnabled)GD.Print($"[particle-grid] sample={i} label={s.Label} effect={s.Effect} library={s.Library} cell={pos} timing={(s.Moving?"moving-gallery-loop":_repeatOneShot[i]?(authoredContinuous?$"authored-mixed/restart-one-shots-{OneShotRepeatSeconds:G}s":$"authored-one-shot/restart-{OneShotRepeatSeconds:G}s"):"authored-continuous")}");
            if(s.Moving)try{var effect=player.PlayAuthored(s.Effect,cell,Vector3.Zero,true,true);_playbacks[i]=effect;_moving=effect;_movingCenter=Vector3.Zero;}catch(KeyNotFoundException){GD.PrintErr($"Missing grid effect '{s.Effect}' from {s.Library}.");}
            else SpawnAuthored(i);
            AddLabel(s.Label,pos+new Vector3(0,LabelYOffset,3));
        }
        var screenshot=args.GetValueOrDefault("screenshot","");if(screenshot.Length>0){await ToSignal(GetTree().CreateTimer(6.4),SceneTreeTimer.SignalName.Timeout);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);var full=Path.GetFullPath(screenshot);Directory.CreateDirectory(Path.GetDirectoryName(full)!);var error=GetViewport().GetTexture().GetImage().SavePng(full);GD.Print($"Saved particle grid screenshot to {full} (Error={error}).");GetTree().Quit(error==Error.Ok?0:1);}
    }
    public override void _Process(double delta)
    {
        var d=(float)delta;_time+=d;_repeat+=d;
        if(_repeat>=OneShotRepeatSeconds){_repeat-=OneShotRepeatSeconds;for(var i=0;i<Samples.Length;i++)if(_repeatOneShot[i])_playbacks[i]?.RestartOneShots();}
        if(_moving==null)return;var r=1.7f;_moving.Position=_movingCenter+new Vector3(MathF.Cos(_time*2)*r,MathF.Sin(_time*2)*r,0);_moving.RotationDegrees=new Vector3(0,0,_time*Mathf.RadToDeg(2)+90);
    }
    private void SpawnAuthored(int index)
    {
        var sample=Samples[index];var cell=_cells[index];if(_player==null||cell==null)return;try{_playbacks[index]=_player.PlayAuthored(sample.Effect,cell,Vector3.Zero,true);}catch(KeyNotFoundException){if(_time==0)GD.PrintErr($"Missing grid effect '{sample.Effect}' from {sample.Library}.");}
    }
    private static Vector3 Cell(int i){var col=i%GridColumns;var row=i/GridColumns;return new((col-(GridColumns-1)*.5f)*CellWidth,GridTop-row*CellHeight,0);}
    private void AddWorld()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment{BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color(.32f,.32f,.32f),AmbientLightSource=Godot.Environment.AmbientSource.Color,AmbientLightColor=new Color(.7f,.72f,.75f),AmbientLightEnergy=.55f,TonemapMode=Godot.Environment.ToneMapper.Filmic}});
        AddChild(new DirectionalLight3D{Name="GalleryKeyLight",RotationDegrees=new Vector3(-35,-30,0),LightColor=new Color(1,.94f,.85f),LightEnergy=1.25f,ShadowEnabled=true});
    }
    private void AddCamera(){var c=new Camera3D{Name="GridCamera",Projection=Camera3D.ProjectionType.Orthogonal,Size=CameraOrthogonalSize,Near=.1f,Far=200,Position=CameraPosition,Current=true};AddChild(c);c.LookAt(CameraTarget,Vector3.Up);}
    private void AddLabel(string text,Vector3 p)=>AddChild(new Label3D{Text=text,Position=p,FontSize=34,PixelSize=.012f,Billboard=BaseMaterial3D.BillboardModeEnum.Enabled,NoDepthTest=true,RenderPriority=127,Modulate=Colors.White,OutlineSize=5,OutlineModulate=new Color(.08f,.08f,.08f)});
    private static Dictionary<string,string> Args(IEnumerable<string> a){var d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var x in a){var s=x.TrimStart('-').Split('=',2);if(s.Length==2)d[s[0]]=s[1];}return d;}
}
