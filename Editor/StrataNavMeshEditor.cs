using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Strata.Editor
{
    // StrataNavMesh's inspector: the agent type of each Agent Types entry picked from the project's NavMesh agent types
    // by name, with a warning when two entries build for the same type.
    [CustomEditor(typeof(StrataNavMesh))]
    public class StrataNavMeshEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty list = serializedObject.FindProperty("_agentTypes");

            int count = NavMesh.GetSettingsCount();
            int[] ids = new int[count];
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = NavMesh.GetSettingsByIndex(i).agentTypeID;
                names[i] = NavMesh.GetSettingsNameFromID(ids[i]);
            }

            EditorGUILayout.LabelField("Agent Types", EditorStyles.boldLabel);
            HashSet<int> seen = new();
            bool duplicate = false;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty entry = list.GetArrayElementAtIndex(i);
                SerializedProperty id = entry.FindPropertyRelative("AgentTypeID");
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        int current = System.Array.IndexOf(ids, id.intValue);
                        int picked = EditorGUILayout.Popup("Agent Type", current, names);
                        if (picked >= 0 && picked != current) id.intValue = ids[picked];
                        if (GUILayout.Button("Remove", GUILayout.Width(64f)))
                        {
                            list.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                    if (System.Array.IndexOf(ids, id.intValue) < 0) EditorGUILayout.HelpBox($"Agent type {id.intValue} doesn't exist.", MessageType.Warning);
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Area"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("VoxelSize"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("TileSize"));
                }
                if (!seen.Add(id.intValue)) duplicate = true;
            }
            if (duplicate) EditorGUILayout.HelpBox("Two entries build for the same agent type; their NavMeshes overlap.", MessageType.Warning);
            if (GUILayout.Button("Add Agent Type"))
            {
                list.InsertArrayElementAtIndex(list.arraySize);
                SerializedProperty added = list.GetArrayElementAtIndex(list.arraySize - 1);
                added.FindPropertyRelative("AgentTypeID").intValue = count > 0 ? ids[0] : 0;
                added.FindPropertyRelative("Area").intValue = 0;
                added.FindPropertyRelative("VoxelSize").floatValue = 0f;
                added.FindPropertyRelative("TileSize").intValue = 0;
            }

            EditorGUILayout.Space();
            DrawPropertiesExcluding(serializedObject, "m_Script", "_agentTypes");
            serializedObject.ApplyModifiedProperties();
        }
    }
}
