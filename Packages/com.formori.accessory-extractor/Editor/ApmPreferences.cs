using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AccessoryPrefabMaker
{
    public static class ApmPreferences
    {
        static string FolderKey { get { return "APFM.OutputFolder." + Application.dataPath; } }
        public static string OutputFolder
        {
            get
            {
                string value=EditorPrefs.GetString(FolderKey,"Assets");
                return (value=="Assets" || value.StartsWith("Assets/",StringComparison.Ordinal)) && AssetDatabase.IsValidFolder(value) ? value : "Assets";
            }
        }
        public static string AssetFolder(string path)
        {
            if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException(ApmLocalization.T("존재하는 프로젝트 Assets 폴더를 지정하세요."));
            string full=Path.GetFullPath(path).Replace('\\','/').TrimEnd('/');
            string assets=Path.GetFullPath(Application.dataPath).Replace('\\','/').TrimEnd('/');
            if(!full.Equals(assets,StringComparison.OrdinalIgnoreCase) && !full.StartsWith(assets+"/",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(ApmLocalization.T("현재 프로젝트의 Assets 폴더 내부를 선택하세요."));
            string relative="Assets"+full.Substring(assets.Length);
            if(!AssetDatabase.IsValidFolder(relative)) throw new ArgumentException(ApmLocalization.T("존재하는 프로젝트 Assets 폴더를 지정하세요."));
            return relative;
        }
        public static void SetOutputFolder(string path) { EditorPrefs.SetString(FolderKey,AssetFolder(path)); }
    }
}
