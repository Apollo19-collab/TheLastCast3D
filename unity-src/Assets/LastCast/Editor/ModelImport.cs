// Import settings for the Blender models (blender/lastcast_models.py writes them to
// Resources/Models): just the meshes, in metres, with their flat normals and
// vertex colours. The game draws them with its own materials.

using UnityEditor;

namespace LastCast.EditorTools
{
    public class ModelImport : AssetPostprocessor
    {
        public const string Folder = "Assets/LastCast/Resources/Models";

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var mi = (ModelImporter)assetImporter;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importBlendShapes = false;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.isReadable = true; // fish and pets are recoloured from their templates
            mi.weldVertices = false;
            mi.globalScale = 1;
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
        }
    }
}
