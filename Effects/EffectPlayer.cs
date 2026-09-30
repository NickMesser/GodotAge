#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Effects;

/// <summary>Scene player for parsed CE3 effects, including nested timing, attachments and pooled one-shots.</summary>
public sealed partial class EffectPlayer : Node3D
{
    private sealed class Playback
    {
        public required GpuParticles3D Emitter; public float Delay,InitialDelay,Pulse,PulseInterval,Remaining,InitialRemaining; public bool Continuous, Started;
    }
    private readonly Dictionary<string, ParticleLibrary> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParticleEffectDefinition> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stack<EffectPlayer>> _oneShotPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Playback> _emitters = [];
    private ParticleEffectDefinition? _current; private bool? _loopOverride; private bool _emitterPathTrails; private float _autoFree = -1;
    private EffectPlayer? _poolOwner; private Node? _attachmentShell; private string _poolKey = "";

    [Export] public string LibraryPath { get; set; } = "";
    [Export] public string EffectName { get; set; } = "";
    [Export] public bool Autoplay { get; set; }
    [Export] public bool Underwater { get; set; }
    public ParticleFactory Factory { get; set; } = new();
    /// <summary>
    /// Skips emitters whose material is a refraction ring ("p_ref_*"): the engine draws them as a distortion, this player has no
    /// distortion pass and would show a white disc. Off by default; the login stage turns it on.
    /// </summary>
    public bool SkipRefractionMaterials { get; set; }
    /// <summary>
    /// Skips emitters with a Multiplicative BlendType: this player's multiply blend darkens the whole card wherever the texture is black, so
    /// glow textures meant to multiply as light show as black rectangles. Off by default; the login stage turns it on.
    /// </summary>
    public bool SkipMultiplicativeMaterials { get; set; }
    /// <summary>Skips emitters whose Geometry path contains one of these (meshes this player cannot draw yet, e.g. opacity-mapped ones). Empty by default.</summary>
    public string[] SkipGeometry { get; set; } = [];
    /// <summary>
    /// Plays an authored effect even when its top node has Enabled=false (some library roots are only containers switched off in the editor
    /// while their children play, e.g. login_magic_firerain_hit, which the original client shows). Off by default; the login stage turns it on.
    /// </summary>
    public bool IgnoreDisabledRoot { get; set; }
    /// <summary>
    /// Skips emitters with a TailLength whose particles never move (Speed 0, no gravity): the client draws no tail for them, and this player
    /// renders such a particle as a bright vertical streak with a floor glow (the lobby's Login_particle_blue/trail dust). Off by default.
    /// </summary>
    public bool SkipStaticTails { get; set; }
    /// <summary>Skips the emitters with these exact names (ones this player draws far larger than authored). Empty by default.</summary>
    public string[] SkipEmitters { get; set; } = [];
    // dev knobs to isolate emitters by name: X2_FX_EMITTER_ONLY=a,b keeps only emitters whose name contains one of them, X2_FX_EMITTER_SKIP drops those
    private static readonly string[] EmitterOnly = (System.Environment.GetEnvironmentVariable("X2_FX_EMITTER_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static readonly string[] EmitterSkip = (System.Environment.GetEnvironmentVariable("X2_FX_EMITTER_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool IsPlaying { get; private set; }
    public IReadOnlyDictionary<string, ParticleEffectDefinition> Effects => _effects;

    public override void _Ready()
    {
        if (LibraryPath.Length > 0) LoadLibrary(LibraryPath);
        if (Autoplay && EffectName.Length > 0) Play(EffectName);
    }
    public bool LoadLibrary(string path)
    {
        if (!ParticleLibraryReader.TryLoad(path, out var lib)) return false;
        AddLibrary(lib); return true;
    }
    /// <summary>Registers a library parsed elsewhere (ParticleLibraryReader.TryLoad is safe on a worker thread).</summary>
    public void AddLibrary(ParticleLibrary lib)
    {
        _libraries[lib.SourcePath] = lib;
        foreach (var x in lib.Effects) _effects[x.Key] = x.Value;
    }
    public int LoadLibraries(IEnumerable<string> paths) { var n=0; foreach(var p in paths) if(LoadLibrary(p))n++; return n; }
    public bool Play(string name) { if(!_effects.TryGetValue(name,out var e)){GD.PrintErr($"Particle effect '{name}' is not present in the loaded libraries.");return false;}Play(e);return true; }

    public Node3D Play(string name, Node3D attachTo, string? bone, Vector3 offset, bool loop, bool emitterPathTrails=false)
    {
        ArgumentNullException.ThrowIfNull(attachTo); if(!_effects.TryGetValue(name,out var effect))throw new KeyNotFoundException($"Particle effect '{name}' is not present in the loaded libraries.");
        EffectPlayer instance;
        if(!loop&&_oneShotPool.TryGetValue(name,out var pool)&&pool.TryPop(out var reused)) instance=reused;
        else instance=new EffectPlayer{Name=SafeName(effect.Name+" Playback"),Factory=Factory,SkipRefractionMaterials=SkipRefractionMaterials,SkipMultiplicativeMaterials=SkipMultiplicativeMaterials,SkipGeometry=SkipGeometry,IgnoreDisabledRoot=IgnoreDisabledRoot,SkipStaticTails=SkipStaticTails,SkipEmitters=SkipEmitters};
        instance._loopOverride=loop;instance._emitterPathTrails=emitterPathTrails;instance._poolOwner=loop?null:this;instance._poolKey=name;instance._attachmentShell=null;instance.Underwater=Underwater;
        Node3D parent=attachTo;
        if(!string.IsNullOrWhiteSpace(bone)&&FindSkeleton(attachTo,bone,out var skeleton,out var resolved))
        {
            var shell=new BoneAttachment3D{Name=SafeName(effect.Name+" "+resolved+" Attachment"),BoneName=resolved};skeleton.AddChild(shell);parent=shell;instance._attachmentShell=shell;
        }
        parent.AddChild(instance);instance.Position=offset;instance._autoFree=loop?-1:Math.Max(.1f,LongestDuration(effect)+.15f);instance.Play(effect);return instance;
    }
    /// <summary>Plays each emitter with its authored Continuous flag. Finite effects are pooled when complete.</summary>
    public EffectPlayer PlayAuthored(string name,Node3D attachTo,Vector3 offset,bool keepAlive=false,bool emitterPathTrails=false)
    {
        ArgumentNullException.ThrowIfNull(attachTo);if(!_effects.TryGetValue(name,out var effect))throw new KeyNotFoundException($"Particle effect '{name}' is not present in the loaded libraries.");
        var continuous=HasContinuous(effect);EffectPlayer instance;
        if(!continuous&&_oneShotPool.TryGetValue(name,out var pool)&&pool.TryPop(out var reused))instance=reused;else instance=new EffectPlayer{Name=SafeName(effect.Name+" Playback"),Factory=Factory,SkipRefractionMaterials=SkipRefractionMaterials,SkipMultiplicativeMaterials=SkipMultiplicativeMaterials,SkipGeometry=SkipGeometry,IgnoreDisabledRoot=IgnoreDisabledRoot,SkipStaticTails=SkipStaticTails,SkipEmitters=SkipEmitters};
        instance._loopOverride=null;instance._emitterPathTrails=emitterPathTrails;instance._poolOwner=continuous||keepAlive?null:this;instance._poolKey=name;instance._attachmentShell=null;instance.Underwater=Underwater;
        attachTo.AddChild(instance);instance.Position=offset;instance._autoFree=continuous||keepAlive?-1:Math.Max(.1f,LongestDuration(effect)+.15f);instance.Play(effect);return instance;
    }
    /// <summary>Seconds until every emitter of the authored effect has finished, or -1 when some emitter runs until it is stopped.</summary>
    public float AuthoredDuration(string name)=>_effects.TryGetValue(name,out var effect)?(RunsUntilStopped(effect)?-1f:LongestDuration(effect)):0f;
    private static bool RunsUntilStopped(ParticleEffectDefinition e)
    {
        if(!e.Enabled)return false;var p=e.Parameters;
        if((p.Texture.Length>0||p.Material.Length>0||p.Geometry.Length>0)&&p.Continuous&&Math.Max(0,p.EmitterLifeTime.Base)<=0)return true;
        return e.Children.Any(RunsUntilStopped);
    }
    public bool IsAuthoredContinuous(string name)=>_effects.TryGetValue(name,out var effect)&&HasContinuous(effect);
    public bool IsAuthoredOneShot(string name)=>_effects.TryGetValue(name,out var effect)&&HasOneShot(effect);
    public Node3D PlayAt(string name,Vector3 position){var host=GetTree()?.CurrentScene as Node3D??GetParent() as Node3D??this;var n=Play(name,host,null,Vector3.Zero,false);n.GlobalPosition=position;return n;}

    public void Play(ParticleEffectDefinition effect)
    {
        ClearPlayback();_current=effect;EffectName=effect.Name;Build(this,effect,0);IsPlaying=_emitters.Count>0;SetProcess(IsPlaying||_autoFree>=0);
        foreach(var p in _emitters.Where(x=>x.Delay<=0))Start(p);
    }
    public void Restart(){if(_current!=null)Play(_current);}
    public void RestartOneShots()
    {
        // Leave delayed emitters pending. Resetting their delay every two seconds can prevent delays > 2 s forever.
        foreach(var p in _emitters.Where(x=>!x.Continuous&&x.Started)){p.Emitter.Emitting=false;p.Started=false;p.Delay=p.InitialDelay;p.Pulse=p.PulseInterval;p.Remaining=p.InitialRemaining;if(p.Delay<=0)Start(p);}
    }
    public void Stop(bool removeEmitters=false){foreach(var p in _emitters)p.Emitter.Emitting=false;IsPlaying=false;if(removeEmitters)ClearPlayback();}
    public override void _Process(double delta)
    {
        var d=(float)delta;
        foreach(var p in _emitters)
        {
            if(!p.Started){p.Delay-=d;if(p.Delay<=0)Start(p);continue;}
            if(p.PulseInterval>0){p.Pulse-=d;if(p.Pulse<=0){if((int)p.Emitter.GetMeta("particle_budget",0)>0){p.Emitter.Emitting=false;p.Emitter.Emitting=true;p.Remaining=p.InitialRemaining;}p.Pulse+=p.PulseInterval;}}
            if(p.Remaining>0&&(p.Remaining-=d)<=0)p.Emitter.Emitting=false;
        }
        if(_autoFree>=0&&(_autoFree-=d)<=0){_autoFree=-1;CallDeferred(nameof(RecycleOneShot));}
    }
    public void RecycleOneShot()
    {
        Stop();ClearPlayback(); if(_poolOwner==null){QueueFree();return;}var parent=GetParent();parent?.RemoveChild(this);
        if(_attachmentShell!=null&&GodotObject.IsInstanceValid(_attachmentShell))_attachmentShell.QueueFree();_attachmentShell=null;
        if(!_poolOwner._oneShotPool.TryGetValue(_poolKey,out var stack))_poolOwner._oneShotPool[_poolKey]=stack=[];stack.Push(this);
    }

    private void Build(Node3D parent,ParticleEffectDefinition effect,float inheritedDelay)
    {
        var p=effect.Parameters;
        if(!(effect.Enabled||IgnoreDisabledRoot&&ReferenceEquals(effect,_current))||!ParticleFactory.IsVisibleInWater(p.Text("VisibleUnderwater"),Underwater))return;
        var branch=new Node3D{Name=SafeName(effect.Name)};ApplyTransform(branch,effect.Parameters,effect.SpawnParameters);parent.AddChild(branch);
        var delay=inheritedDelay+Math.Max(0,p.SpawnDelay.Base)+Math.Max(0,effect.SpawnParameters.SpawnDelay.Base);
        var hasVisual=p.Texture.Length>0||p.Material.Length>0||p.Geometry.Length>0;
        if(SkipRefractionMaterials&&p.Material.Contains("/p_ref_",StringComparison.OrdinalIgnoreCase))hasVisual=false;
        if(SkipMultiplicativeMaterials&&p.Blend.Contains("multip",StringComparison.OrdinalIgnoreCase))hasVisual=false;
        if(SkipEmitters.Length>0&&SkipEmitters.Any(x=>effect.Name.Equals(x,StringComparison.OrdinalIgnoreCase)))hasVisual=false;
        if(SkipStaticTails&&p.TailLength>0&&!ParticleFactory.ParticleMoves(p))hasVisual=false;
        if(EmitterOnly.Length>0&&!EmitterOnly.Any(x=>effect.Name.Equals(x,StringComparison.OrdinalIgnoreCase)))hasVisual=false;
        if(EmitterSkip.Length>0&&EmitterSkip.Any(x=>effect.Name.Equals(x,StringComparison.OrdinalIgnoreCase)))hasVisual=false;
        if(SkipGeometry.Length>0&&p.Geometry.Length>0&&SkipGeometry.Any(g=>p.Geometry.Contains(g,StringComparison.OrdinalIgnoreCase)))hasVisual=false;
        // The moving sword gallery explicitly uses an emitter-path ribbon. Other TailLength emitters retain their
        // authored sprite and use GPUParticles3D's per-particle trail draw pass.
        if(hasVisual&&(!_emitterPathTrails||p.TailLength<=0))
        {
            var emitter=Factory.CreateEmitter(effect,false);var continuous=p.Continuous;var pulse=Math.Max(0,p.PulsePeriod.Base);
            if(_loopOverride==false){continuous=false;pulse=0;emitter.OneShot=true;emitter.Explosiveness=1;}else if(_loopOverride==true){continuous=true;emitter.OneShot=false;}
            var remaining=_loopOverride.HasValue?0:Math.Max(0,p.EmitterLifeTime.Base);branch.AddChild(emitter);Factory.PrintDebug(effect,emitter);_emitters.Add(new(){Emitter=emitter,Delay=delay,InitialDelay=delay,Pulse=pulse,PulseInterval=pulse,Continuous=continuous,Remaining=remaining,InitialRemaining=remaining});
        }
        if(_emitterPathTrails&&p.TailLength>0){var trail=Factory.CreateTrail(branch,p);branch.AddChild(trail);Factory.PrintTrailDebug(effect,branch);}
        foreach(var child in effect.Children)Build(branch,child,delay);
    }
    private static void Start(Playback p){if(p.Started)return;p.Started=true;if((int)p.Emitter.GetMeta("particle_budget",0)<=0)return;if(p.Continuous&&p.Pulse<=0)p.Emitter.Emitting=true;else p.Emitter.Restart();}
    private void ClearPlayback()
    {
        foreach(var p in _emitters)Factory.ReleaseEmitter(p.Emitter);_emitters.Clear();foreach(var child in GetChildren().OfType<Node>()){RemoveChild(child);child.QueueFree();}IsPlaying=false;
    }
    private static void ApplyTransform(Node3D n,ParticleParameters p,ParticleParameters spawn)
    {
        n.Position=CryVector(p.Text("PositionOffset","Offset","EmitterOffset"));var a=CryVector(p.Text("InitAngles"));n.RotationDegrees=new Vector3(a.X,a.Y,a.Z);
        var spawnScale=Math.Max(0,spawn.Number(1f,"Scale","EmitterScale"));n.Scale=Vector3.One*spawnScale;
        var facing=p.Facing.ToLowerInvariant();if(facing.Contains("horizontal")||facing.Contains("decal")||facing.Contains("bottom"))n.RotationDegrees+=new Vector3(-90,0,0);
    }
    private static Vector3 CryVector(string s){var a=(s??"").Split(',',StringSplitOptions.TrimEntries);float V(int i)=>i<a.Length&&ParticleLibraryReader.TryFloat(a[i],out var v)?v:0;return new(V(0),V(2),-V(1));}
    private static bool FindSkeleton(Node3D root,string bone,out Skeleton3D skeleton,out string resolved)
    {
        if(root is Skeleton3D s&&Resolve(s,bone,out resolved)){skeleton=s;return true;}foreach(var c in root.GetChildren().OfType<Node3D>())if(FindSkeleton(c,bone,out skeleton,out resolved))return true;skeleton=null!;resolved="";return false;
    }
    private static bool Resolve(Skeleton3D s,string name,out string found){var i=s.FindBone(name);if(i>=0){found=s.GetBoneName(i).ToString();return true;}for(i=0;i<s.GetBoneCount();i++){var x=s.GetBoneName(i).ToString();if(x.Equals(name,StringComparison.OrdinalIgnoreCase)){found=x;return true;}}found="";return false;}
    private static float LongestDuration(ParticleEffectDefinition e)
    {
        var own=Math.Max(0,e.Parameters.SpawnDelay.Maximum)+Math.Max(0,e.SpawnParameters.SpawnDelay.Maximum)+Math.Max(0,e.Parameters.EmitterLifeTime.Maximum)+Math.Max(.01f,e.Parameters.LifeTime.Maximum);foreach(var c in e.Children)own=Math.Max(own,Math.Max(0,e.Parameters.SpawnDelay.Maximum)+LongestDuration(c));return own;
    }
    private static bool HasContinuous(ParticleEffectDefinition e)=>(e.Enabled&&e.Parameters.Continuous)||e.Children.Any(HasContinuous);
    private static bool HasOneShot(ParticleEffectDefinition e)=>(e.Enabled&&!e.Parameters.Continuous&&(e.Parameters.Texture.Length>0||e.Parameters.Material.Length>0||e.Parameters.Geometry.Length>0))||e.Children.Any(HasOneShot);
    private static string SafeName(string s)=>string.IsNullOrWhiteSpace(s)?"Particle":s.Replace('/','_').Replace(':','_');
}
