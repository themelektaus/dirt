using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

using FileInfo = System.IO.FileInfo;
using Path = System.IO.Path;
using CallerFilePath = System.Runtime.CompilerServices.CallerFilePathAttribute;

namespace Dirt
{
    public class DirtWindow : EditorWindow
    {
        [SerializeField] VisualTreeAsset visualTreeAsset;
        [SerializeField] VisualTreeAsset headerAsset;
        [SerializeField] VisualTreeAsset rowAsset;

        [MenuItem("Tools/Dirt")]
        public static void Open() => GetWindow<DirtWindow>("Dirt", typeof(SceneView));

        static DirtWindowSettings settings;
        static void LoadSettings([CallerFilePath] string path = null)
        {
            path = Path.GetRelativePath(
                    System.Environment.CurrentDirectory,
                    new FileInfo(path).Directory.FullName
                );
            path = Path.Combine(path, $"{nameof(DirtWindowSettings)}.asset");

            settings = AssetDatabase.LoadAssetAtPath<DirtWindowSettings>(path);

            if (settings)
                return;

            settings = CreateInstance<DirtWindowSettings>();
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.Refresh();
        }

        string activePath;
        int refreshCountdown;

        TextField searchField;
        ScrollView content;

        readonly Dictionary<GameObject, List<Modification>> cache = new();
        readonly List<Modification> visibleModifications = new();

        public void CreateGUI()
        {
            LoadSettings();

            if (!visualTreeAsset)
                return;

            var templateContainer = visualTreeAsset.Instantiate();
            rootVisualElement.Add(templateContainer);
            templateContainer.StretchToParentSize();

            searchField = rootVisualElement.Q<TextField>("Search");
            searchField.RegisterValueChangedCallback(e => Refresh(useCache: true));

            content = rootVisualElement.Q<ScrollView>("Content");
            content.RegisterCallback<MouseDownEvent>(e =>
            {
                if (e.button != 1)
                    return;

                var menu = new GenericMenu();
                AddDefaultItemsTo(menu);
                menu.ShowAsContext();
            });
        }

        public class Modification
        {
            public string propertyPath;

            public class Reference
            {
                public GameObject owner;
                public Object target;

                public string targetPath { get; private set; }
                public List<int> targetIndexedPath { get; private set; }
                public string targetPropertyValue { get; private set; }
                public Object targetPropertyObjectValue { get; private set; }
                public bool isNothing { get; private set; }
                public bool hasValue { get; private set; }

                public SerializedProperty GetTargetProperty(Modification modification)
                {
                    if (!target)
                    {
                        Debug.LogWarning(owner);
                        return null;
                    }

                    var prefabObject = new SerializedObject(target);
                    return prefabObject.FindProperty(modification.propertyPath);
                }

                public int Setup(Modification modification)
                {
                    var componentIndex = -1;

                    if (owner && target)
                    {
                        var parent = target is Transform transform
                            ? transform
                            : target is GameObject gameObject
                                ? gameObject.transform
                                : (target as Component).transform;

                        var components = parent.GetComponents<Component>();
                        componentIndex = ArrayUtility.IndexOf(components, target);

                        var path = new List<string>();
                        targetIndexedPath = new List<int>();

                        while (parent && parent != owner.transform)
                        {
                            path.Insert(0, parent.name);
                            targetIndexedPath.Insert(0, parent.GetSiblingIndex());
                            parent = parent.parent;
                        }

                        targetPath = string.Join("/", path);
                    }
                    else
                    {
                        targetPath = string.Empty;
                    }

                    var p = GetTargetProperty(modification);
                    string v;

                    if (p is null)
                    {
                        isNothing = true;
                        v = "<color=#999999>(nothing)</color>";
                    }
                    else
                    {
                        hasValue = true;

                        switch (p.type)
                        {
                            case "bool":
                                v = $"<color=#9999ff>{p.boolValue}</color>";
                                break;

                            case "short":
                            case "int":
                            case "uint":
                                v = $"<color=#33ff33>{p.intValue}</color>";
                                break;

                            case "float":
                                v = $"<color=#99ff99>{p.floatValue:0.00}</color>";
                                break;

                            case "string":
                                v = p.stringValue == string.Empty
                                    ? $"<color=#cc9900>(empty)</color>"
                                    : $"<color=#ffcc66>{p.stringValue}</color>";
                                break;

                            case "Enum":
                                if (0 <= p.enumValueIndex && p.enumValueIndex < p.enumNames.Length)
                                    v = $"<color=#ffff00>{p.enumNames[p.enumValueIndex]}</color>";
                                else
                                    v = $"<color=#ffff00>[type Enum] {p.enumValueIndex}</color>";
                                break;

                            case "ArraySize":
                                v = p.intValue.ToString();
                                break;

                            default:
                                if (Regex.IsMatch(p.type, @"^PPtr\<.+\>$"))
                                {
                                    targetPropertyObjectValue = p.objectReferenceValue;
                                    v = targetPropertyObjectValue
                                        ? $"<color=#33ffff>{targetPropertyObjectValue.name}</color>"
                                        : "<color=#009999>(null)</color>";
                                    break;
                                }

                                v = $"[type {p.type}]";
                                v = $"<color=#999999>{v}</color>";
                                hasValue = false;
                                break;
                        }
                    }

                    targetPropertyValue = v;

                    return componentIndex;
                }
            }

            public Reference instance;
            public Reference prefab;
            public bool excluded;
            bool appliedOrReverted;

            public Modification(GameObject prefabOwner, GameObject instanceOwner, PropertyModification propertyModification)
            {
                propertyPath = propertyModification.propertyPath;

                prefab = new()
                {
                    owner = prefabOwner,
                    target = propertyModification.target
                };

                var componentIndex = prefab.Setup(this);

                var exclusionState = settings.GetExclusionState(this);
                if (exclusionState == DirtWindowSettings.ExclusionState.Excluded)
                    return;

                excluded = exclusionState != DirtWindowSettings.ExclusionState.Included;

                var instanceTransform = instanceOwner.transform;
                instance = new()
                {
                    owner = instanceOwner,
                    target = instanceTransform
                };

                var target = instanceTransform;

                if (prefab.targetPath != string.Empty)
                    target = target.Find(prefab.targetPath);

                if (!target)
                {
                    target = instanceTransform;
                    var indexed = prefab.targetIndexedPath.ToList();
                    while (target && indexed.Count > 0)
                    {
                        var i = indexed[0];
                        target = i < target.childCount ? target.GetChild(i) : null;
                        indexed.RemoveAt(0);
                    }
                }

                if (target)
                {
                    if (prefab.target is GameObject)
                        instance.target = target.gameObject;
                    else if (componentIndex > -1)
                    {
                        var components = target.GetComponents<Component>();
                        if (componentIndex < components.Length)
                            instance.target = components[componentIndex];
                    }
                }

                instance.Setup(this);
            }

            public bool IsUnchangedButDirty()
            {
                if (appliedOrReverted)
                    return false;

                if (prefab.isNothing && instance.isNothing)
                    return true;

                if (!prefab.hasValue)
                    return false;

                if (!instance.hasValue)
                    return false;

                if (prefab.targetPropertyValue != instance.targetPropertyValue)
                    return false;

                if (prefab.targetPropertyObjectValue != instance.targetPropertyObjectValue)
                    return false;

                return true;
            }

            public void Apply()
            {
                appliedOrReverted = true;

                PrefabUtility.ApplyPropertyOverride(
                    instance.GetTargetProperty(this),
                    AssetDatabase.GetAssetPath(prefab.owner),
                    InteractionMode.UserAction
                );
            }

            public void Revert()
            {
                appliedOrReverted = true;

                var p = instance.GetTargetProperty(this);
                if (p is not null)
                {
                    PrefabUtility.RevertPropertyOverride(p, InteractionMode.UserAction);
                    return;
                }

                PrefabUtility.RemoveUnusedOverrides(
                    new[] { instance.owner },
                    InteractionMode.UserAction
                );
            }
        }

        void OnInspectorUpdate()
        {
            var activePath = GetActivePath(out _);
            if (this.activePath != activePath)
            {
                this.activePath = activePath;
                refreshCountdown = 2;
            }

            if (refreshCountdown == 0)
                return;

            if (refreshCountdown == 1)
                Refresh(useCache: false);

            refreshCountdown--;
        }

        string GetActivePath(out PrefabStage prefabStage)
        {
            prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage)
                return prefabStage.assetPath;

            return string.Join(";", Enumerable
                .Range(0, SceneManager.sceneCount)
                .Select(i => SceneManager.GetSceneAt(i).path)
            );
        }

        void Refresh(bool useCache)
        {
            content.Clear();

            if (!useCache)
            {
                activePath = GetActivePath(out var prefabStage);

                var instanceOwners = Enumerable.Empty<GameObject>();

                if (prefabStage)
                {
                    instanceOwners = instanceOwners.Concat(
                        prefabStage
                            .FindComponentsOfType<Transform>()
                            .Where(x => x.hideFlags != HideFlags.HideAndDontSave)
                            .Select(x => x.gameObject)
                    );
                }
                else
                {
                    instanceOwners = instanceOwners.Concat(
                        FindObjectsByType<GameObject>(FindObjectsSortMode.InstanceID)
                    );
                }

                instanceOwners = instanceOwners.Where(
                    PrefabUtility.IsOutermostPrefabInstanceRoot
                );

                cache.Clear();
                foreach (var instanceOwner in instanceOwners)
                    cache.Add(instanceOwner, new());
            }

            visibleModifications.Clear();

            foreach (var instanceOwner in cache.Keys)
            {
                if (!useCache)
                {
                    var prefabOwner = PrefabUtility.GetCorrespondingObjectFromSource(instanceOwner);

                    cache[instanceOwner].AddRange(
                        PrefabUtility.GetPropertyModifications(instanceOwner)
                            .Select(x => new Modification(prefabOwner, instanceOwner, x))
                            .Where(x => x.instance is not null)
                    );
                }

                var filteredModifications = cache[instanceOwner].Where(x =>
                {
                    var s = searchField.value;
                    var i = System.StringComparison.InvariantCultureIgnoreCase;

                    if (instanceOwner.name.Contains(s, i))
                        return true;

                    var t = x.instance.target;

                    if (t && (t.name.Contains(s, i) || t.GetType().Name.Contains(s, i)))
                        return true;

                    if (x.propertyPath.Contains(s, i))
                        return true;

                    if (x.prefab.targetPropertyValue.Contains(s, i))
                        return true;

                    if (x.instance.targetPropertyValue.Contains(s, i))
                        return true;

                    return false;
                }).ToList();

                if (settings.showOnlyUnchangedDirtyValues)
                {
                    filteredModifications = filteredModifications
                        .Where(x => x.IsUnchangedButDirty())
                        .ToList();
                }

                if (filteredModifications.Count == 0)
                    continue;

                visibleModifications.AddRange(filteredModifications);


                var header = headerAsset.Instantiate();
                content.Add(header);

                var instanceOwnerField = header.Q<ObjectField>("InstanceOwner");
                instanceOwnerField.value = instanceOwner;
                instanceOwnerField.SetEnabled(false);

                foreach (var x in filteredModifications)
                {
                    var row = rowAsset.Instantiate();
                    content.Add(row);

                    if (x.excluded)
                        row.style.color = new Color(1, .5f, .5f, .5f);

                    row.RegisterCallback<MouseDownEvent>(e =>
                    {
                        if (e.button != 1)
                            return;

                        e.StopPropagation();

                        var menu = new GenericMenu();
                        menu.AddItem(new("Revert"), false, x =>
                        {
                            (x as Modification).Revert();
                            row.SetEnabled(false);
                        }, x);
                        menu.AddItem(new("Apply"), false, x =>
                        {
                            (x as Modification).Apply();
                            row.SetEnabled(false);
                        }, x);

                        menu.AddSeparator("");
                        if (x.excluded)
                        {
                            menu.AddItem(new("Excluded"), true, x =>
                            {
                                var m = x as Modification;
                                settings.exclusions.RemoveAll(x => x.Match(m));
                                refreshCountdown++;
                            }, x);
                        }
                        else
                        {
                            menu.AddItem(new("Excluded"), false, x =>
                            {
                                var m = x as Modification;
                                settings.exclusions.Add(new()
                                {
                                    owner = m.prefab.owner,
                                    targetType = m.prefab.target.GetType().FullName,
                                    useTargetPath = true,
                                    targetPath = m.prefab.targetPath,
                                    propertyPath = m.propertyPath
                                });
                                refreshCountdown++;
                            }, x);
                        }
                        menu.AddSeparator("");
                        AddDefaultItemsTo(menu);
                        menu.ShowAsContext();
                    });
                    row.Q<ObjectField>("InstanceTarget").value = x.instance.target;

                    Label label;
                    ObjectField objectField;

                    label = row.Q<Label>("PrefabValue");
                    objectField = row.Q<ObjectField>("PrefabObjectValue");

                    if (x.prefab.targetPropertyObjectValue)
                    {
                        label.RemoveFromHierarchy();
                        objectField.value = x.prefab.targetPropertyObjectValue;
                    }
                    else
                    {
                        label.text = x.prefab.targetPropertyValue;
                        objectField.RemoveFromHierarchy();
                    }

                    label = row.Q<Label>("InstanceValue");
                    objectField = row.Q<ObjectField>("InstanceObjectValue");

                    if (x.instance.targetPropertyObjectValue)
                    {
                        label.RemoveFromHierarchy();
                        objectField.value = x.instance.targetPropertyObjectValue;
                    }
                    else
                    {
                        label.text = x.instance.targetPropertyValue;
                        objectField.RemoveFromHierarchy();
                    }

                    var propertyPathLabel = row.Q<Label>("PropertyPath");
                    propertyPathLabel.text = x.propertyPath;

                    if (x.IsUnchangedButDirty())
                    {
                        if (!x.excluded)
                            row.style.color = Color.white;
                        row.style.backgroundColor = new Color(.5f, 1, .5f, .1f);
                        row.RegisterCallback<MouseOverEvent>(e => row.style.backgroundColor = new Color(.5f, 1, .5f, .15f));
                        row.RegisterCallback<MouseOutEvent>(e => row.style.backgroundColor = new Color(.5f, 1, .5f, .1f));
                    }
                    else
                    {
                        row.RegisterCallback<MouseOverEvent>(e => row.style.backgroundColor = new Color(1, 1, 1, .05f));
                        row.RegisterCallback<MouseOutEvent>(e => row.style.backgroundColor = new(StyleKeyword.Undefined));
                    }
                }
            }

            content.Add(new Label());
            content.Add(new Label());
        }

        void AddDefaultItemsTo(GenericMenu menu)
        {
            menu.AddItem(new("Refresh"), false, () => refreshCountdown++);

            var modifications = visibleModifications
                .Where(x => x.IsUnchangedButDirty())
                .ToList();

            if (modifications.Count > 0)
            {
                menu.AddItem(new($"Revert Unchanged Values ({modifications.Count})"), false, () =>
                {
                    var tempMenu = new GenericMenu();
                    tempMenu.AddDisabledItem(new("Continue?"));
                    tempMenu.AddItem(new("✔ Yes"), false, () =>
                    {
                        foreach (var modification in modifications)
                            modification.Revert();

                        refreshCountdown++;
                    });
                    tempMenu.AddItem(new("❌ No"), false, () => { });
                    tempMenu.ShowAsContext();
                });
            }

            menu.AddItem(new("Show Only Unchanged Dirty Values"), settings.showOnlyUnchangedDirtyValues, () =>
            {
                settings.showOnlyUnchangedDirtyValues = !settings.showOnlyUnchangedDirtyValues;
                refreshCountdown++;
            });
            menu.AddItem(new("Show Exclusions"), settings.showExclusions, () =>
            {
                settings.showExclusions = !settings.showExclusions;
                refreshCountdown++;
            });
            menu.AddItem(new("Settings"), false, () => Selection.activeObject = settings);
        }
    }
}
