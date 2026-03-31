using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RAM.Models;

namespace RAM.Services;

public sealed class CSharpGenerationValidationService
{
    private static readonly HashSet<string> LayoutContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Border",
        "DockPanel",
        "Frame",
        "Grid",
        "GroupBox",
        "ItemsControl",
        "ListBox",
        "ListView",
        "ScrollViewer",
        "StackPanel",
        "TabControl",
        "TabItem",
        "UniformGrid",
        "WrapPanel"
    };

    public List<string> BuildProfileRequirements(CSharpGenerationPromptContractRecord contract)
    {
        List<string> requirements = contract.Profile switch
        {
            CSharpGenerationProfile.ContractGeneration =>
            [
                "declare the required namespace and contract type",
                "include real member signatures for required members",
                "avoid empty or marker-only contract shells"
            ],
            CSharpGenerationProfile.SimpleImplementation =>
            [
                "declare the required namespace and implementation type",
                "include at least one meaningful implemented member body",
                "avoid structurally thin one-member shells"
            ],
            CSharpGenerationProfile.TestRegistryImplementation =>
            [
                "declare a concrete registry type with default checks plus lookup behavior",
                "include CreateDefaultChecks, Contains, and FindByKey with real bounded case-insensitive lookup logic",
                "avoid mostly empty helper shells that only echo input"
            ],
            CSharpGenerationProfile.SnapshotBuilderImplementation =>
            [
                "declare a concrete snapshot builder with default snapshot state plus JSON rendering helpers",
                "include BuildDefaultSnapshot, BuildDefaultSnapshotJson, and BuildSnapshotJson with real bounded payload composition logic",
                "avoid thin builder shells that only return a single hard-coded payload"
            ],
            CSharpGenerationProfile.FindingsNormalizerImplementation =>
            [
                "declare a concrete findings normalizer with severity, status, and collection normalization behavior",
                "include NormalizeSeverity, NormalizeStatus, and NormalizeFindings with real bounded transformation logic that reuses the helper methods",
                "avoid thin shells that normalize only one field or pass collections through unchanged"
            ],
            CSharpGenerationProfile.TestHelperImplementation =>
            [
                "declare a concrete helper type with bounded lookup members",
                "include non-empty helper methods for default data and lookup behavior",
                "avoid one-method or constant-only helper shells"
            ],
            CSharpGenerationProfile.BuilderImplementation =>
            [
                "declare a concrete builder type with default data plus serialization helpers",
                "include non-empty builder methods for default state and rendered output",
                "avoid thin builder shells that only return a single hard-coded string"
            ],
            CSharpGenerationProfile.NormalizerImplementation =>
            [
                "declare a concrete normalizer type with normalization helpers",
                "include non-empty severity, status, and collection normalization methods",
                "avoid thin shells that normalize only one field"
            ],
            CSharpGenerationProfile.RepositoryImplementation =>
            [
                "declare a concrete repository/store implementation type",
                "include a persistent path field plus non-empty read and write methods that use the configured storage path",
                "use real storage behavior instead of constant-return shortcuts"
            ],
            CSharpGenerationProfile.ViewmodelGeneration =>
            [
                "declare binding-ready state members",
                "include real properties or commands needed by the requested view surface"
            ],
            CSharpGenerationProfile.WpfXamlStubOnly =>
            [
                "placeholder scaffold only; no stronger layout enforcement required"
            ],
            CSharpGenerationProfile.WpfXamlLayoutImplementation =>
            [
                "emit a real page or user control root with non-trivial layout containers",
                "include required bindings and templated item or section structure where requested",
                "avoid effectively empty markup"
            ],
            CSharpGenerationProfile.WpfViewmodelImplementation =>
            [
                "declare real state-bearing public members for bindings",
                "include interaction methods that mutate or expose meaningful state when the contract requires them",
                "avoid thin shells that only name the type"
            ],
            CSharpGenerationProfile.WpfShellIntegration =>
            [
                "emit a real window shell with DataContext and navigation/state bindings",
                "include multi-region layout and routed shell sections that point at real generated page surfaces",
                "avoid empty shell windows"
            ],
            CSharpGenerationProfile.RuntimeWiring =>
            [
                "emit the expected wiring type with real members for the target file role",
                "align the wiring shape to the local runtime contract instead of generic shells"
            ],
            _ => []
        };

        if (string.Equals(contract.DeclaredPattern, "controller", StringComparison.OrdinalIgnoreCase))
        {
            requirements.Add("controller contract requires route attributes, injected service dependency, and at least one real read/write endpoint surface");
        }

        if (string.Equals(contract.DeclaredPattern, "worker_support", StringComparison.OrdinalIgnoreCase))
        {
            requirements.Add("worker support contract requires a BackgroundService-derived type with deterministic execute loop behavior");
        }

        if (string.Equals(contract.DeclaredPattern, "dto", StringComparison.OrdinalIgnoreCase))
        {
            requirements.Add("dto contract requires stable public request/response members with no placeholder-only shell");
        }

        if (!string.IsNullOrWhiteSpace(contract.ImplementationDepth))
            requirements.Add($"implementation depth target: {contract.ImplementationDepth}");
        if (!string.IsNullOrWhiteSpace(contract.DeclaredPattern))
            requirements.Add($"declared pattern: {contract.DeclaredPattern}");
        if (contract.FollowThroughRequirements.Count > 0)
            requirements.Add($"follow-through expectations: {string.Join(", ", contract.FollowThroughRequirements)}");

        if (string.Equals(contract.DeclaredPattern, "repository", StringComparison.OrdinalIgnoreCase)
            && contract.ImplementationDepth is "standard" or "strong")
        {
            requirements.Add("repository depth requires constructor or helper-based storage path management plus non-trivial read and write behavior");
        }

        if (string.Equals(contract.DeclaredPattern, "viewmodel", StringComparison.OrdinalIgnoreCase)
            && contract.ImplementationDepth is "standard" or "strong")
        {
            requirements.Add("viewmodel depth requires binding-ready state plus at least one interaction method that mutates or selects meaningful state");
        }

        if (string.Equals(contract.DeclaredPattern, "page", StringComparison.OrdinalIgnoreCase)
            && contract.ImplementationDepth is "standard" or "strong")
        {
            requirements.Add("page depth requires named layout regions and multiple live bindings into the intended viewmodel surface");
        }

        if (string.Equals(contract.DeclaredPattern, "test_harness", StringComparison.OrdinalIgnoreCase)
            && string.Equals(contract.ImplementationDepth, "strong", StringComparison.OrdinalIgnoreCase))
        {
            requirements.Add("strong test harness depth requires deterministic default data plus bounded lookup or transformation helpers");
        }

        return requirements;
    }

    public CSharpGenerationProfileEnforcementRecord Validate(
        CSharpGenerationPromptContractRecord contract,
        string content)
    {
        if (contract is null || !contract.Applicable)
        {
            return new CSharpGenerationProfileEnforcementRecord
            {
                Status = "not_applicable",
                Summary = "profile_enforcement not_applicable"
            };
        }

        var failedRules = new List<string>();
        var observedSignals = new List<string>();
        var extension = Path.GetExtension(contract.TargetPath);

        if (string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            ValidateXaml(contract, content, failedRules, observedSignals);
        }
        else
        {
            ValidateCSharp(contract, content, failedRules, observedSignals);
        }

        var status = failedRules.Count == 0 ? "passed" : "rejected";
        var summary = failedRules.Count == 0
            ? $"profile_enforcement passed for {FormatProfile(contract.Profile)}"
            : $"profile_enforcement rejected for {FormatProfile(contract.Profile)}: {string.Join(", ", failedRules)}";

        return new CSharpGenerationProfileEnforcementRecord
        {
            Status = status,
            FailedRules = failedRules,
            ObservedSignals = observedSignals,
            Summary = summary
        };
    }

    private static void ValidateCSharp(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var normalizedContent = content ?? "";
        if (!HasNamespaceDeclaration(normalizedContent))
            failedRules.Add("namespace_declaration_missing");
        else
            observedSignals.Add("namespace_declared");

        ValidateRequiredTypeDeclarations(contract, normalizedContent, failedRules, observedSignals);
        ValidateRequiredMemberDeclarations(contract, normalizedContent, failedRules, observedSignals);

        switch (contract.Profile)
        {
            case CSharpGenerationProfile.ContractGeneration:
                ValidateContractGeneration(contract, normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.SimpleImplementation:
                ValidateSimpleImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.TestRegistryImplementation:
                ValidateTestRegistryImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.SnapshotBuilderImplementation:
                ValidateSnapshotBuilderImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.FindingsNormalizerImplementation:
                ValidateFindingsNormalizerImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.TestHelperImplementation:
                ValidateTestHelperImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.BuilderImplementation:
                ValidateBuilderImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.NormalizerImplementation:
                ValidateNormalizerImplementation(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.RepositoryImplementation:
                ValidateRepositoryImplementation(contract, normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.ViewmodelGeneration:
                ValidateViewmodelGeneration(normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.WpfViewmodelImplementation:
                ValidateWpfViewmodelImplementation(contract, normalizedContent, failedRules, observedSignals);
                break;
            case CSharpGenerationProfile.WpfShellIntegration:
            case CSharpGenerationProfile.RuntimeWiring:
                ValidateRuntimeWiring(contract, normalizedContent, failedRules, observedSignals);
                break;
        }

        ValidateDeclaredPatternDepth(contract, normalizedContent, failedRules, observedSignals);
    }

    private static void ValidateXaml(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        if (!TryParseXaml(content, out var document))
        {
            failedRules.Add("xaml_parse_failed");
            return;
        }

        var rootName = document.Root?.Name.LocalName ?? "";
        if (!string.IsNullOrWhiteSpace(rootName))
            observedSignals.Add($"root={rootName}");

        var elementNames = document.Descendants().Select(element => element.Name.LocalName).ToList();
        var elementCount = elementNames.Count;
        var layoutContainerCount = elementNames.Count(name => LayoutContainerNames.Contains(name));
        var bindingCount = Regex.Matches(content ?? "", @"\{Binding[^}]+\}", RegexOptions.CultureInvariant).Count;
        var hasTemplate = ContainsAny(content, "ItemTemplate", "ListView.View", "DataTemplate");

        observedSignals.Add($"elements={elementCount}");
        observedSignals.Add($"layout_containers={layoutContainerCount}");
        observedSignals.Add($"bindings={bindingCount}");
        if (hasTemplate)
            observedSignals.Add("templated_items=true");

        switch (contract.Profile)
        {
            case CSharpGenerationProfile.WpfXamlLayoutImplementation:
                if (!string.Equals(rootName, "Page", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(rootName, "UserControl", StringComparison.OrdinalIgnoreCase))
                {
                    failedRules.Add("xaml_layout_root_invalid");
                }

                if (layoutContainerCount < 2)
                    failedRules.Add("xaml_layout_container_depth_too_shallow");
                if (elementCount < 6)
                    failedRules.Add("xaml_layout_element_count_too_low");
                if (bindingCount < Math.Max(2, contract.RequiredApiTokens.Count == 0 ? 1 : 2))
                    failedRules.Add("xaml_layout_binding_surface_too_thin");
                if (ContainsAny(content, "ItemsSource=\"", "DisplayMemberBinding=\"") && !hasTemplate)
                    failedRules.Add("xaml_layout_item_presentation_missing");
                if (Regex.Matches(content ?? "", @"x:Name=""[^""]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count == 0)
                    failedRules.Add("xaml_layout_named_region_missing");
                break;

            case CSharpGenerationProfile.WpfShellIntegration:
                if (!string.Equals(rootName, "Window", StringComparison.OrdinalIgnoreCase))
                    failedRules.Add("wpf_shell_root_invalid");
                if (!ContainsAny(content, "<Window.DataContext>", "<Window.DataContext "))
                    failedRules.Add("wpf_shell_datacontext_missing");
                if (layoutContainerCount < 4)
                    failedRules.Add("wpf_shell_layout_too_thin");
                if (bindingCount < 4)
                    failedRules.Add("wpf_shell_binding_surface_too_thin");
                if (!ContainsAny(content, "ItemsSource=\"{Binding State.NavigationItems}\"", "ItemsSource=\"{Binding NavigationItems}\""))
                    failedRules.Add("wpf_shell_navigation_binding_missing");
                if (Regex.Matches(content ?? "", @"<TabItem\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count < 4)
                    failedRules.Add("wpf_shell_tabbed_sections_missing");
                if (Regex.Matches(content ?? "", @"<Frame\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count < 4)
                    failedRules.Add("wpf_shell_routed_sections_missing");
                if (!ContainsAny(content, "<ListBox", "<ItemsControl"))
                    failedRules.Add("wpf_shell_navigation_surface_missing");
                break;
        }
    }

    private static void ValidateContractGeneration(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var interfaceCount = Regex.Matches(content, @"\binterface\s+[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant).Count;
        var memberSignatureCount = Regex.Matches(content, @"\b[A-Za-z0-9_<>,\[\]\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\)\s*;", RegexOptions.CultureInvariant).Count
            + Regex.Matches(content, @"\b[A-Za-z0-9_<>,\[\]\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\{\s*(get|set|init)", RegexOptions.CultureInvariant).Count;

        observedSignals.Add($"interface_count={interfaceCount}");
        observedSignals.Add($"contract_member_signatures={memberSignatureCount}");

        if (Path.GetFileName(contract.TargetPath).StartsWith("I", StringComparison.OrdinalIgnoreCase) && interfaceCount == 0)
            failedRules.Add("contract_interface_declaration_missing");
        if (contract.RequiredMemberNames.Count > 0 && memberSignatureCount < contract.RequiredMemberNames.Count)
            failedRules.Add("contract_member_surface_incomplete");
    }

    private static void ValidateSimpleImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var propertyCount = CountPublicPropertyDeclarations(content);
        var nonEmptyMethodCount = CountNonEmptyMethodBodies(content);
        observedSignals.Add($"public_properties={propertyCount}");
        observedSignals.Add($"implemented_methods={nonEmptyMethodCount}");

        if (propertyCount + nonEmptyMethodCount < 2)
            failedRules.Add("simple_implementation_too_thin");
    }

    private static void ValidateRepositoryImplementation(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var fieldCount = Regex.Matches(content, @"\bprivate\s+(?:readonly\s+)?[A-Za-z0-9_<>,\[\]\?]+\s+_[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.CultureInvariant).Count;
        var methodCount = CountNonEmptyMethodBodies(content);
        var hasReadPath = Regex.IsMatch(content, @"return\s+File\.Exists\([^;]+\)\s*\?\s*File\.ReadAllText\(", RegexOptions.CultureInvariant)
            || Regex.IsMatch(content, @"return\s+File\.ReadAllText\(", RegexOptions.CultureInvariant);
        var hasWritePath = Regex.IsMatch(content, @"File\.WriteAllText\(", RegexOptions.CultureInvariant);
        var returnsConstant = Regex.IsMatch(content, @"return\s+""[^""]*""\s*;", RegexOptions.CultureInvariant)
            || Regex.IsMatch(content, @"return\s+string\.Empty\s*;", RegexOptions.CultureInvariant);
        var usesPathCombine = Regex.IsMatch(content, @"Path\.Combine\(", RegexOptions.CultureInvariant);
        var usesBaseDirectory = Regex.IsMatch(content, @"AppContext\.BaseDirectory", RegexOptions.CultureInvariant);
        var usesStorageFieldInRead = Regex.IsMatch(content, @"File\.ReadAllText\(\s*_[A-Za-z_][A-Za-z0-9_]*\s*\)", RegexOptions.CultureInvariant);
        var usesStorageFieldInWrite = Regex.IsMatch(content, @"File\.WriteAllText\(\s*_[A-Za-z_][A-Za-z0-9_]*\s*,", RegexOptions.CultureInvariant);

        observedSignals.Add($"private_fields={fieldCount}");
        observedSignals.Add($"implemented_methods={methodCount}");
        if (hasReadPath)
            observedSignals.Add("repository_read_path=true");
        if (hasWritePath)
            observedSignals.Add("repository_write_path=true");
        if (usesPathCombine)
            observedSignals.Add("repository_path_combine=true");
        if (usesBaseDirectory)
            observedSignals.Add("repository_base_directory=true");

        if (!Regex.IsMatch(content, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant))
            failedRules.Add("repository_interface_implementation_missing");
        if (fieldCount == 0)
            failedRules.Add("repository_storage_field_missing");
        if (methodCount < 2)
            failedRules.Add("repository_method_surface_too_thin");
        if (!hasReadPath)
            failedRules.Add("repository_read_behavior_missing");
        if (!hasWritePath)
            failedRules.Add("repository_write_behavior_missing");
        if (!usesPathCombine)
            failedRules.Add("repository_path_resolution_missing");
        if (!usesBaseDirectory)
            failedRules.Add("repository_runtime_base_path_missing");
        if (!usesStorageFieldInRead || !usesStorageFieldInWrite)
            failedRules.Add("repository_storage_field_not_used");
        if (returnsConstant && !hasReadPath)
            failedRules.Add("repository_constant_return_behavior");
        if (returnsConstant && !Regex.IsMatch(content, @"return\s+File\.Exists\([^;]+\)\s*\?\s*File\.ReadAllText\(", RegexOptions.CultureInvariant))
            failedRules.Add("repository_read_result_not_used");

        ValidateSimpleImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateTestHelperImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var methodCount = CountNonEmptyMethodBodies(content);
        var hasDefaultCollection = ContainsAny(content, "return", "new List<", "[");
        observedSignals.Add($"implemented_methods={methodCount}");
        if (hasDefaultCollection)
            observedSignals.Add("helper_default_collection=true");

        if (methodCount < 3)
            failedRules.Add("test_helper_method_surface_too_thin");
        if (!hasDefaultCollection)
            failedRules.Add("test_helper_default_data_missing");

        ValidateSimpleImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateTestRegistryImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        if (!HasNonEmptyMethodNamed(content, "CreateDefaultChecks"))
            failedRules.Add("test_registry_default_factory_missing");
        if (!HasNonEmptyMethodNamed(content, "Contains"))
            failedRules.Add("test_registry_contains_missing");
        if (!HasNonEmptyMethodNamed(content, "FindByKey"))
            failedRules.Add("test_registry_lookup_missing");

        var expectedCheckCount = Regex.Matches(content ?? "", "\"(?:defender|firewall|updates)\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count;
        observedSignals.Add($"test_registry_default_checks={expectedCheckCount}");
        if (expectedCheckCount < 3)
            failedRules.Add("test_registry_default_checks_missing");
        if (!Regex.IsMatch(content ?? "", @"Contains\s*\([^)]*\)\s*\{(?<body>.*?)FindByKey\(", RegexOptions.CultureInvariant | RegexOptions.Singleline))
            failedRules.Add("test_registry_contains_not_linked_to_lookup");
        if (!ContainsAny(content, "StringComparison.OrdinalIgnoreCase"))
            failedRules.Add("test_registry_case_insensitive_lookup_missing");

        ValidateTestHelperImplementation(content ?? "", failedRules, observedSignals);
    }

    private static void ValidateBuilderImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var methodCount = CountNonEmptyMethodBodies(content);
        var hasSerialization = ContainsAny(content, "JsonSerializer.Serialize(", "BuildSnapshotJson(");
        var hasDefaultState = ContainsAny(content, "BuildDefaultSnapshot(", "Dictionary<", "new Dictionary<");
        observedSignals.Add($"implemented_methods={methodCount}");
        if (hasSerialization)
            observedSignals.Add("builder_serialization=true");
        if (hasDefaultState)
            observedSignals.Add("builder_default_state=true");

        if (methodCount < 3)
            failedRules.Add("builder_method_surface_too_thin");
        if (!hasSerialization)
            failedRules.Add("builder_serialization_behavior_missing");
        if (!hasDefaultState)
            failedRules.Add("builder_default_state_missing");

        ValidateSimpleImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateSnapshotBuilderImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        if (!HasNonEmptyMethodNamed(content, "BuildDefaultSnapshotJson"))
            failedRules.Add("snapshot_builder_default_json_missing");
        if (!HasNonEmptyMethodNamed(content, "BuildDefaultSnapshot"))
            failedRules.Add("snapshot_builder_default_state_missing");
        if (!HasNonEmptyMethodNamed(content, "BuildSnapshotJson"))
            failedRules.Add("snapshot_builder_render_method_missing");

        if (!ContainsAny(content, "\"checks\"", "\"capturedBy\"", "\"machine\""))
            failedRules.Add("snapshot_builder_payload_shape_missing");
        if (!ContainsAny(content, "BuildSnapshotJson(BuildDefaultSnapshot())"))
            failedRules.Add("snapshot_builder_default_json_flow_missing");
        if (!ContainsAny(content, "JsonSerializer.Serialize("))
            failedRules.Add("snapshot_builder_json_serialization_missing");

        ValidateBuilderImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateNormalizerImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var methodCount = CountNonEmptyMethodBodies(content);
        var hasCollectionNormalization = ContainsAny(content, "foreach", "List<", "Array.Empty<");
        var hasStringNormalization = ContainsAny(content, "ToLowerInvariant(", "string.IsNullOrWhiteSpace(");
        observedSignals.Add($"implemented_methods={methodCount}");
        if (hasCollectionNormalization)
            observedSignals.Add("normalizer_collection_behavior=true");
        if (hasStringNormalization)
            observedSignals.Add("normalizer_string_behavior=true");

        if (methodCount < 3)
            failedRules.Add("normalizer_method_surface_too_thin");
        if (!hasCollectionNormalization)
            failedRules.Add("normalizer_collection_behavior_missing");
        if (!hasStringNormalization)
            failedRules.Add("normalizer_string_behavior_missing");

        ValidateSimpleImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateFindingsNormalizerImplementation(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        if (!HasNonEmptyMethodNamed(content, "NormalizeSeverity"))
            failedRules.Add("findings_normalizer_severity_method_missing");
        if (!HasNonEmptyMethodNamed(content, "NormalizeStatus"))
            failedRules.Add("findings_normalizer_status_method_missing");
        if (!HasNonEmptyMethodNamed(content, "NormalizeFindings"))
            failedRules.Add("findings_normalizer_collection_method_missing");

        if (!ContainsAny(content, "Trim(", "Trim()", "ToLowerInvariant(", "switch"))
            failedRules.Add("findings_normalizer_string_transform_missing");
        if (!ContainsAny(content, "NormalizeSeverity(", "NormalizeStatus("))
            failedRules.Add("findings_normalizer_collection_reuse_missing");
        if (!ContainsAny(content, "foreach", "Select("))
            failedRules.Add("findings_normalizer_collection_iteration_missing");

        ValidateNormalizerImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateViewmodelGeneration(
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var propertyCount = CountPublicPropertyDeclarations(content);
        observedSignals.Add($"public_properties={propertyCount}");
        if (propertyCount < 2)
            failedRules.Add("viewmodel_generation_surface_too_thin");
    }

    private static void ValidateWpfViewmodelImplementation(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var propertyCount = CountPublicPropertyDeclarations(content);
        var methodCount = CountNonEmptyMethodBodies(content);
        observedSignals.Add($"public_properties={propertyCount}");
        observedSignals.Add($"implemented_methods={methodCount}");

        if (propertyCount < Math.Max(3, Math.Min(4, contract.RequiredMemberNames.Count)))
            failedRules.Add("wpf_viewmodel_state_surface_too_thin");
        if (contract.RequiredMemberNames.Contains("Navigate", StringComparer.OrdinalIgnoreCase)
            && !HasNonEmptyMethodNamed(content, "Navigate"))
        {
            failedRules.Add("wpf_viewmodel_navigation_method_missing");
        }
        if (ContainsAny(contract.TargetPath, "shellviewmodel.cs")
            && !ContainsAny(content, "State.CurrentRoute ="))
        {
            failedRules.Add("wpf_viewmodel_navigation_state_mutation_missing");
        }

        ValidateSimpleImplementation(content, failedRules, observedSignals);
    }

    private static void ValidateDeclaredPatternDepth(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var pattern = contract.DeclaredPattern ?? "";
        var depth = contract.ImplementationDepth ?? "";
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(depth))
            return;

        observedSignals.Add($"declared_pattern={pattern}");
        observedSignals.Add($"declared_depth={depth}");

        switch (pattern.ToLowerInvariant())
        {
            case "repository" when depth is "standard" or "strong":
            {
                var constructorCount = Regex.Matches(content, @"\bpublic\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\)", RegexOptions.CultureInvariant).Count;
                var privateHelperCount = Regex.Matches(content, @"\bprivate\s+(?:static\s+)?[A-Za-z0-9_<>,\[\]\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\)\s*\{", RegexOptions.CultureInvariant).Count;
                observedSignals.Add($"constructors={constructorCount}");
                observedSignals.Add($"private_helpers={privateHelperCount}");
                if (constructorCount == 0)
                    failedRules.Add("repository_constructor_missing_for_declared_depth");
                if (privateHelperCount == 0)
                    failedRules.Add("repository_helper_methods_missing_for_declared_depth");
                break;
            }

            case "viewmodel" when depth is "standard" or "strong":
            {
                if (CountPublicPropertyDeclarations(content) < 4)
                    failedRules.Add("viewmodel_binding_surface_too_thin_for_declared_depth");
                if (CountNonEmptyMethodBodies(content) < 2)
                    failedRules.Add("viewmodel_interaction_methods_missing_for_declared_depth");
                break;
            }

            case "page" when depth is "standard" or "strong":
            {
                var bindingCount = Regex.Matches(content ?? "", @"\{Binding[^}]+\}", RegexOptions.CultureInvariant).Count;
                if (bindingCount < 2)
                    failedRules.Add("page_binding_surface_too_thin_for_declared_depth");
                if (Regex.Matches(content ?? "", @"x:Name=""[^""]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase).Count == 0)
                    failedRules.Add("page_named_regions_missing_for_declared_depth");
                break;
            }

            case "test_harness" when string.Equals(depth, "strong", StringComparison.OrdinalIgnoreCase):
            {
                if (CountNonEmptyMethodBodies(content) < 3)
                    failedRules.Add("test_harness_helper_surface_too_thin_for_declared_depth");
                if (!ContainsAny(content, "StringComparison.OrdinalIgnoreCase", "JsonSerializer.Serialize(", "Normalize")
                    && !ContainsAny(content, "Assert.", "Fact]", "Theory]"))
                {
                    failedRules.Add("test_harness_behavior_helpers_missing_for_declared_depth");
                }

                break;
            }

            case "service" when depth is "standard" or "strong":
            {
                if (CountNonEmptyMethodBodies(content) < 2)
                    failedRules.Add("service_behavior_surface_too_thin_for_declared_depth");
                if (Regex.Matches(content ?? "", @"\bprivate\s+readonly\b", RegexOptions.CultureInvariant).Count == 0)
                    failedRules.Add("service_dependency_or_state_field_missing_for_declared_depth");
                break;
            }

            case "controller" when depth is "standard" or "strong":
            {
                if (!ContainsAny(content, "[ApiController]", "[Route("))
                    failedRules.Add("controller_route_surface_missing_for_declared_depth");
                if (Regex.Matches(content ?? "", @"\[Http(Get|Post|Put|Delete)", RegexOptions.CultureInvariant).Count < 2)
                    failedRules.Add("controller_endpoint_surface_too_thin_for_declared_depth");
                break;
            }

            case "worker_support" when depth is "standard" or "strong":
            {
                if (!ContainsAny(content, "BackgroundService", "ExecuteAsync"))
                    failedRules.Add("worker_support_runtime_surface_missing_for_declared_depth");
                break;
            }
        }
    }

    private static void ValidateRuntimeWiring(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var fileName = Path.GetFileName(contract.TargetPath);
        switch (fileName.ToLowerInvariant())
        {
            case "navigationitem.cs":
                if (CountPublicPropertyDeclarations(content) < 2)
                    failedRules.Add("navigation_item_surface_too_thin");
                break;
            case "shellnavigationregistry.cs":
                if (!HasNonEmptyMethodNamed(content, "CreateDefault"))
                    failedRules.Add("navigation_registry_factory_missing");
                if (Regex.Matches(content, @"new\(\)\s*\{", RegexOptions.CultureInvariant).Count < 4)
                    failedRules.Add("navigation_registry_entries_missing");
                break;
            default:
                ValidateSimpleImplementation(content, failedRules, observedSignals);
                break;
        }
    }

    private static void ValidateRequiredTypeDeclarations(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        var primaryTypeName = ResolvePrimaryTypeName(contract);
        foreach (var typeName in contract.RequiredTypeNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(typeName, primaryTypeName, StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsIdentifier(content, typeName))
                    observedSignals.Add($"type_ref={typeName}");
                continue;
            }

            if (!ContainsDeclaredType(content, typeName))
            {
                failedRules.Add($"expected_type_declaration_missing:{typeName}");
                continue;
            }

            observedSignals.Add($"type={typeName}");
        }
    }

    private static void ValidateRequiredMemberDeclarations(
        CSharpGenerationPromptContractRecord contract,
        string content,
        ICollection<string> failedRules,
        ICollection<string> observedSignals)
    {
        foreach (var memberName in contract.RequiredMemberNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!HasMemberDeclaration(content, memberName))
            {
                failedRules.Add($"expected_member_declaration_missing:{memberName}");
                continue;
            }

            observedSignals.Add($"member={memberName}");
        }
    }

    private static bool TryParseXaml(string content, out XDocument document)
    {
        try
        {
            document = XDocument.Parse(content ?? "");
            return true;
        }
        catch
        {
            document = new XDocument();
            return false;
        }
    }

    private static bool HasNamespaceDeclaration(string content)
    {
        return Regex.IsMatch(
            content ?? "",
            @"^\s*namespace\s+[A-Za-z_][A-Za-z0-9_\.]*\s*(?:;|\{)",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
    }

    private static bool ContainsDeclaredType(string content, string typeName)
    {
        return Regex.IsMatch(
            content ?? "",
            $@"\b(?:class|interface|record|struct)\s+{Regex.Escape(typeName)}\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ContainsIdentifier(string content, string identifier)
    {
        return Regex.IsMatch(
            content ?? "",
            $@"\b{Regex.Escape(identifier)}\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HasMemberDeclaration(string content, string memberName)
    {
        return Regex.IsMatch(
            content ?? "",
            $@"\b{Regex.Escape(memberName)}\s*(?:\(|\{{|=>)",
            RegexOptions.CultureInvariant);
    }

    private static int CountPublicPropertyDeclarations(string content)
    {
        return Regex.Matches(
                content ?? "",
                @"\bpublic\s+(?:static\s+)?[A-Za-z0-9_<>,\[\]\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\{\s*(?:get|set|init)",
                RegexOptions.CultureInvariant)
            .Count;
    }

    private static int CountNonEmptyMethodBodies(string content)
    {
        return Regex.Matches(
                content ?? "",
                @"\b(?:public|private|internal|protected)\s+(?:static\s+|sealed\s+|override\s+|virtual\s+|async\s+)*[A-Za-z0-9_<>,\[\]\?]+\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\)\s*\{(?<body>.*?)\}",
                RegexOptions.CultureInvariant | RegexOptions.Singleline)
            .Cast<Match>()
            .Count(match => !string.IsNullOrWhiteSpace(StripBraces(match.Groups["body"].Value)));
    }

    private static bool HasNonEmptyMethodNamed(string content, string methodName)
    {
        return Regex.Matches(
                content ?? "",
                $@"\b(?:public|private|internal|protected)\s+(?:static\s+|sealed\s+|override\s+|virtual\s+|async\s+)*[A-Za-z0-9_<>,\[\]\?]+\s+{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{(?<body>.*?)\}}",
                RegexOptions.CultureInvariant | RegexOptions.Singleline)
            .Cast<Match>()
            .Any(match => !string.IsNullOrWhiteSpace(StripBraces(match.Groups["body"].Value)));
    }

    private static string StripBraces(string value)
    {
        return (value ?? "")
            .Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal)
            .Trim();
    }

    private static bool ContainsAny(string? content, params string[] values)
    {
        foreach (var value in values)
        {
            if ((content ?? "").Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FormatProfile(CSharpGenerationProfile profile)
    {
        return profile switch
        {
            CSharpGenerationProfile.ContractGeneration => "contract_generation",
            CSharpGenerationProfile.SimpleImplementation => "simple_implementation",
            CSharpGenerationProfile.TestRegistryImplementation => "test_registry_impl",
            CSharpGenerationProfile.SnapshotBuilderImplementation => "snapshot_builder_impl",
            CSharpGenerationProfile.FindingsNormalizerImplementation => "findings_normalizer_impl",
            CSharpGenerationProfile.TestHelperImplementation => "test_helper_impl",
            CSharpGenerationProfile.BuilderImplementation => "builder_impl",
            CSharpGenerationProfile.NormalizerImplementation => "normalizer_impl",
            CSharpGenerationProfile.RepositoryImplementation => "repository_implementation",
            CSharpGenerationProfile.ViewmodelGeneration => "viewmodel_generation",
            CSharpGenerationProfile.WpfXamlStubOnly => "wpf_xaml_stub_only",
            CSharpGenerationProfile.WpfXamlLayoutImplementation => "wpf_xaml_layout_impl",
            CSharpGenerationProfile.WpfViewmodelImplementation => "wpf_viewmodel_impl",
            CSharpGenerationProfile.WpfShellIntegration => "wpf_shell_integration",
            CSharpGenerationProfile.RuntimeWiring => "runtime_wiring",
            _ => "none"
        };
    }

    private static string ResolvePrimaryTypeName(CSharpGenerationPromptContractRecord contract)
    {
        var fileStem = Path.GetFileNameWithoutExtension(contract.TargetPath);
        if (!string.IsNullOrWhiteSpace(fileStem))
            return fileStem;

        return contract.RequiredTypeNames.FirstOrDefault() ?? "";
    }
}
