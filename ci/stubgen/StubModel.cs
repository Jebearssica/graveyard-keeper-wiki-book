using Mono.Cecil;

namespace StubGen;

/// <summary>A game type that has to be declared in the stubs, plus the members the plugin links against.</summary>
internal sealed class TypeUsage
{
    public required TypeDefinition Definition { get; init; }
    public required string AssemblyName { get; init; }
    public HashSet<string> Fields { get; } = new();
    public Dictionary<string, PropertyUsage> Properties { get; } = new();
    public Dictionary<string, MethodDefinition> Methods { get; } = new();
    public HashSet<string> Constructors { get; } = new();
}

internal sealed class PropertyUsage
{
    public required PropertyDefinition Property { get; init; }
    public bool Get { get; set; }
    public bool Set { get; set; }
}

internal sealed class StubModel
{
    private const string CsharpAssembly = "Assembly-CSharp";
    private const string FirstpassAssembly = "Assembly-CSharp-firstpass";

    // Members that are only referenced through nameof() in attributes, so they never show up in the IL.
    private static readonly string[] ExtraMembers =
    {
        "MainGame::Update"
    };

    private readonly Dictionary<string, AssemblyDefinition> _games = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeUsage> _types = new(StringComparer.Ordinal);
    private readonly string _managedDir;

    public StubModel(string managedDir)
    {
        _managedDir = managedDir;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        var parameters = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };

        foreach (var name in new[] { CsharpAssembly, FirstpassAssembly })
        {
            _games[name] = AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, name + ".dll"), parameters);
        }
    }

    public int TypeCount => _types.Count;

    public int MemberCount => _types.Values.Sum(t =>
        t.Fields.Count + t.Properties.Count + t.Methods.Count + t.Constructors.Count);

    public void Collect(string pluginPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(_managedDir);
        var plugin = AssemblyDefinition.ReadAssembly(pluginPath,
            new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });

        foreach (var type in plugin.MainModule.GetTypeReferences())
        {
            AddType(type);
        }

        foreach (var type in plugin.MainModule.Types)
        {
            CollectFromType(type);
        }

        foreach (var extra in ExtraMembers)
        {
            var parts = extra.Split("::", 2);
            if (!_types.TryGetValue(parts[0], out var usage))
            {
                continue;
            }

            var method = usage.Definition.Methods.FirstOrDefault(m => m.Name == parts[1]);
            if (method != null)
            {
                AddMember(method, usage);
            }
        }
    }

    public void Write(string outDir)
    {
        foreach (var assemblyName in _games.Keys)
        {
            var usages = _types.Values
                .Where(t => t.AssemblyName == assemblyName && t.Definition.DeclaringType == null)
                .OrderBy(t => t.Definition.FullName, StringComparer.Ordinal)
                .ToList();

            var directory = Path.Combine(outDir, assemblyName);
            Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(Path.Combine(directory, assemblyName + ".Stub.cs")))
            {
                Emitter.Write(writer, assemblyName, usages, _types);
            }

            var version = _games[assemblyName].Name.Version;
            File.WriteAllText(Path.Combine(directory, "AssemblyInfo.cs"),
                "using System.Reflection;\n" +
                $"[assembly: AssemblyVersion(\"{version}\")]\n" +
                $"[assembly: AssemblyFileVersion(\"{version}\")]\n");
        }
    }

    private void CollectFromType(TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MemberReference reference)
                {
                    AddMemberReference(reference);
                }
                else if (instruction.Operand is TypeReference referenceType)
                {
                    AddType(referenceType);
                }
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            CollectFromType(nested);
        }
    }

    private void AddMemberReference(MemberReference reference)
    {
        if (!TryGetGameType(reference.DeclaringType, out var declaring, out var assemblyName))
        {
            return;
        }

        var usage = EnsureType(declaring, assemblyName);

        switch (reference)
        {
            case FieldReference fieldReference:
                AddMember(SafeResolve(fieldReference), usage);
                break;
            case MethodReference methodReference:
                AddMember(SafeResolve(methodReference), usage);
                break;
        }
    }

    private static FieldDefinition? SafeResolve(FieldReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static MethodDefinition? SafeResolve(MethodReference reference)
    {
        try
        {
            return reference.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private void AddMember(FieldDefinition? field, TypeUsage usage)
    {
        if (field == null)
        {
            return;
        }

        usage.Fields.Add(field.Name);
        AddType(field.FieldType);
    }

    private void AddMember(MethodDefinition? method, TypeUsage usage)
    {
        if (method == null)
        {
            return;
        }

        AddType(method.ReturnType);
        foreach (var parameter in method.Parameters)
        {
            AddType(parameter.ParameterType);
        }

        if (method.IsGetter || method.IsSetter)
        {
            var property = method.DeclaringType.Properties.FirstOrDefault(p =>
                p.GetMethod == method || p.SetMethod == method);
            if (property != null)
            {
                if (!usage.Properties.TryGetValue(property.Name, out var propertyUsage))
                {
                    propertyUsage = new PropertyUsage { Property = property };
                    usage.Properties[property.Name] = propertyUsage;
                }

                propertyUsage.Get |= method.IsGetter;
                propertyUsage.Set |= method.IsSetter;
                AddType(property.PropertyType);
                return;
            }
        }

        if (method.IsConstructor)
        {
            usage.Constructors.Add(method.FullName);
            return;
        }

        usage.Methods[method.FullName] = method;
    }

    private void AddType(TypeReference? reference)
    {
        if (!TryGetGameType(reference, out var definition, out var assemblyName))
        {
            return;
        }

        EnsureType(definition, assemblyName);
    }

    private TypeUsage EnsureType(TypeDefinition definition, string assemblyName)
    {
        if (_types.TryGetValue(definition.FullName, out var existing))
        {
            return existing;
        }

        var usage = new TypeUsage { Definition = definition, AssemblyName = assemblyName };
        _types[definition.FullName] = usage;

        if (definition.BaseType != null)
        {
            AddType(definition.BaseType);
        }

        if (definition.DeclaringType != null)
        {
            AddType(definition.DeclaringType);
        }

        return usage;
    }

    private bool TryGetGameType(TypeReference? reference, out TypeDefinition definition, out string assemblyName)
    {
        definition = null!;
        assemblyName = string.Empty;

        while (reference is TypeSpecification specification)
        {
            reference = specification.ElementType;
        }

        if (reference == null || reference is GenericParameter || !TryGetAssemblyName(reference, out assemblyName))
        {
            return false;
        }

        try
        {
            var resolved = reference.Resolve();
            if (resolved == null)
            {
                return false;
            }

            definition = resolved;
            return true;
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }

    private bool TryGetAssemblyName(TypeReference reference, out string assemblyName)
    {
        assemblyName = string.Empty;
        var scopeName = reference.Scope?.Name;
        if (scopeName == null)
        {
            return false;
        }

        // A TypeDefinition's scope is a ModuleDefinition, whose name still carries the file extension.
        scopeName = Path.GetFileNameWithoutExtension(scopeName);
        if (!_games.ContainsKey(scopeName))
        {
            return false;
        }

        assemblyName = scopeName;
        return true;
    }
}
