using System;
using System.Collections.Generic;
using System.Linq;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Dirt
{
    public class DirtWindow : EditorWindow
    {
        [SerializeField] VisualTreeAsset visualTreeAsset;
        [SerializeField] VisualTreeAsset headerAsset;
        [SerializeField] VisualTreeAsset rowAsset;

        [MenuItem("Tools/Dirt")]
        public static void Open() => GetWindow<DirtWindow>("Dirt", typeof(SceneView));

        string activePath;
        int refreshCountdown;

        TextField searchField;
        ScrollView content;

        readonly List<List<Modification>> cache = new();
        readonly List<Modification> visibleModifications = new();

        int cacheIndexGUI;

        public void CreateGUI()
        {
            DirtWindowSettings.Load();

            if (!visualTreeAsset)
                return;

            var templateContainer = visualTreeAsset.Instantiate();
            rootVisualElement.Add(templateContainer);
            templateContainer.StretchToParentSize();

            searchField = rootVisualElement.Q<TextField>("Search");
            searchField.RegisterValueChangedCallback(e => refreshCountdown = 3);

            content = rootVisualElement.Q<ScrollView>("Content");
            content.RegisterCallback<MouseDownEvent>(e => OnMouseDown(e));

            refreshCountdown = 2;
        }

        void Update()
        {
            var text = "Dirt";

            if (cache.Count > 0)
            {
                var progress = Mathf.RoundToInt((float) cacheIndexGUI / cache.Count * 100);
                if (progress < 100)
                    text += $" - {progress}%";
            }

            if (text == "Dirt" && titleContent.text != text)
                Repaint();

            titleContent.text = text;

            if (refreshCountdown > 0)
                return;

            UpdateGUI();
        }

        void OnInspectorUpdate()
        {
            var activePath = GetActivePath(out _);
            if (this.activePath != activePath)
            {
                this.activePath = activePath;
                refreshCountdown += 2;
            }

            if (refreshCountdown == 0)
                return;

            if (refreshCountdown == 1)
            {
                this.activePath = GetActivePath(out var prefabStage);
                var instanceOwners = FindInstanceOwners(prefabStage);
                RefreshCache(instanceOwners);
                ClearGUI();
            }

            refreshCountdown--;
        }

        static string GetActivePath(out PrefabStage prefabStage)
        {
            prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage)
                return prefabStage.assetPath;

            return string.Join(";", Enumerable
                .Range(0, SceneManager.sceneCount)
                .Select(i => SceneManager.GetSceneAt(i).path)
            );
        }

        static List<GameObject> FindInstanceOwners(PrefabStage prefabStage)
        {
            return (prefabStage
                ? prefabStage
                    .FindComponentsOfType<Transform>()
                    .Where(x => x.hideFlags != HideFlags.HideAndDontSave)
                    .Select(x => x.gameObject)
                : FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID)
                    .AsEnumerable()
            ).ToList();
        }

        void RefreshCache(List<GameObject> instanceOwners)
        {
            cache.Clear();

            foreach (var instanceOwner in instanceOwners.Where(PrefabUtility.IsOutermostPrefabInstanceRoot))
            {
                var prefabOwner = PrefabUtility.GetCorrespondingObjectFromSource(instanceOwner);

                cache.Add(
                    PrefabUtility.GetPropertyModifications(instanceOwner)
                        .Select(x => new Modification(prefabOwner, instanceOwner, x))
                        .Where(x => x.instance is not null)
                        .ToList()
                );
            }
        }

        void ClearGUI()
        {
            content.Clear();
            visibleModifications.Clear();
            cacheIndexGUI = 0;
        }

        void UpdateGUI()
        {
            if (cacheIndexGUI >= cache.Count)
                return;

            var settings = DirtWindowSettings.instance;

            foreach (var modifications in cache.Skip(cacheIndexGUI))
            {
                cacheIndexGUI++;

                var filteredModifications = modifications.Where(x =>
                {
                    var s = searchField.value;
                    if (s == string.Empty)
                        return true;

                    var i = StringComparison.InvariantCultureIgnoreCase;

                    var instanceOwner = x.instance.GetOwner();
                    if (instanceOwner && instanceOwner.name.Contains(s, i))
                        return true;

                    var t = x.instance.GetTarget();
                    if (t && (t.name.Contains(s, i) || t.GetType().Name.Contains(s, i)))
                        return true;

                    if (x.propertyPath.Contains(s, i))
                        return true;

                    if (x.prefab.targetPropertyValue.Contains(s, i))
                        return true;

                    if (x.instance.targetPropertyValue.Contains(s, i))
                        return true;

                    return false;
                }).Where(x => x.IsUnchangedButDirty()
                    ? settings.visibility.HasFlag(VisibilityMask.UnchangedDirtyValues)
                    : settings.visibility.HasFlag(VisibilityMask.ChangedDirtyValues)
                ).ToList();

                if (filteredModifications.Count == 0)
                    continue;

                visibleModifications.AddRange(filteredModifications);

                var header = headerAsset.Instantiate();
                content.Add(header);

                var instanceOwnerField = header.Q<ObjectField>("InstanceOwner");
                instanceOwnerField.value = filteredModifications.First().instance.GetOwner();
                instanceOwnerField.SetEnabled(false);

                var rowContent = new VisualElement();
                content.Add(rowContent);

                if (settings.modificationLimit.y > 0 && filteredModifications.Count <= settings.modificationLimit.y)
                {
                    foreach (var m in filteredModifications)
                        rowContent.Add(CreateRow(m));
                    break;
                }

                foreach (var m in filteredModifications.Take(settings.modificationLimit.x))
                    rowContent.Add(CreateRow(m));

                var moreModifications = filteredModifications.Skip(settings.modificationLimit.x).ToList();
                var button = new Button { text = $"Show More... ({moreModifications.Count})" };
                button.AddToClassList("link");
                button.clicked += () =>
                {
                    button.RemoveFromHierarchy();
                    foreach (var m in moreModifications)
                        rowContent.Add(CreateRow(m));
                };
                rowContent.Add(button);

                break;
            }
        }

        VisualElement CreateRow(Modification modification)
        {
            var row = rowAsset.Instantiate();
            row.AddToClassList("row");

            row.RegisterCallback<MouseDownEvent>(e => OnMouseDown(e, row, modification));
            row.Q<ObjectField>("InstanceTarget").value = modification.instance.GetTarget();

            foreach (var (name, reference) in new[] {
                ("Prefab", modification.prefab),
                ("Instance", modification.instance)
            })
            {
                var label = row.Q<Label>($"{name}Value");
                var objectField = row.Q<ObjectField>($"{name}ObjectValue");

                if (reference.hasTargetPropertyObjectValue)
                {
                    label.RemoveFromHierarchy();
                    objectField.value = reference.targetPropertyObjectValue;
                }
                else
                {
                    label.text = reference.targetPropertyValue;
                    objectField.RemoveFromHierarchy();
                }
            }

            var propertyPathLabel = row.Q<Label>("PropertyPath");
            propertyPathLabel.text = modification.propertyPath;

            RefreshRowAppeareance(row, modification);

            return row;
        }

        void RefreshRowAppeareance(VisualElement row, Modification modification)
        {
            if (modification.excluded)
                row.AddToClassList("excluded");
            else
                row.RemoveFromClassList("excluded");

            if (modification.IsUnchangedButDirty())
                row.AddToClassList("unchanged-but-dirty");
            else
                row.RemoveFromClassList("unchanged-but-dirty");
        }

        void OnMouseDown(MouseDownEvent e, VisualElement row = null, Modification modification = null)
        {
            if (e.button != 1)
                return;

            var settings = DirtWindowSettings.instance;

            if (row is not null)
                e.StopPropagation();

            var menu = new GenericMenu();

            if (row is not null)
            {
                menu.AddItem(new("Revert"), false, x =>
                {
                    (x as Modification).Revert();
                    row.SetEnabled(false);
                }, modification);
            }

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

                        refreshCountdown = 1;
                    });
                    tempMenu.AddItem(new("❌ No"), false, () => { });
                    tempMenu.ShowAsContext();
                });
            }

            if (row is not null)
            {
                menu.AddItem(new("Apply"), false, x =>
                {
                    (x as Modification).Apply();
                    row.SetEnabled(false);
                }, modification);

                menu.AddItem(
                    content: new(modification.excluded ? "Excluded" : "Exclude"),
                    on: modification.excluded,
                    func: x =>
                    {
                        var m = x as Modification;
                        settings.ToggleExclusion(m);
                        RefreshRowAppeareance(row, m);
                    },
                    userData: modification
                );
            }

            if (row is not null || modifications.Count > 0)
                menu.AddSeparator("");

            menu.AddItem(new("Refresh"), false, () => refreshCountdown = 1);

            menu.AddSeparator("");

            foreach (var (x, y) in new[] {
                (VisibilityMask.ChangedDirtyValues, "Changed Dirty Values"),
                (VisibilityMask.UnchangedDirtyValues, "Unchanged Dirty Values"),
                (VisibilityMask.Exclusions, "Exclusions")
            })
            {
                var on = settings.visibility.HasFlag(x);
                menu.AddItem(new($"Show {y}"), on, () =>
                {
                    if (on)
                        settings.visibility ^= x;
                    else
                        settings.visibility |= x;
                    refreshCountdown = 1;
                });
            }

            menu.AddSeparator("");

            menu.AddItem(new("Open Settings"), false, () =>
            {
                var activeObject = Selection.activeObject;
                Selection.activeObject = settings;
                EditorApplication.ExecuteMenuItem("Assets/Properties...");
                Selection.activeObject = activeObject;
            });

            menu.ShowAsContext();
        }
    }
}
