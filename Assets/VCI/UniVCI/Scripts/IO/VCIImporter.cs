﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VCIGLTF;
using VCIJSON;
#if UNITY_EDITOR
using UnityEditor;

#endif

namespace VCI
{
    public class VCIImporter : ImporterContext
    {
        public Dictionary<string, string> AudioAssetPathList = new Dictionary<string, string>();

        public List<Effekseer.EffekseerEmitter> EffekseerEmitterComponents = new List<Effekseer.EffekseerEmitter>();
        public List<Effekseer.EffekseerEffectAsset> EffekseerEffectAssets = new List<Effekseer.EffekseerEffectAsset>();

        public VCIObject VCIObject { get; private set; }

        public VCIImporter()
        {
            UseUniJSONParser = true;
        }

        public override void Parse(string path, byte[] bytes)
        {
            var ext = Path.GetExtension(path).ToLower();
            switch (ext)
            {
                case ".vci":
                    ParseGlb(bytes);
                    break;

                default:
                    base.Parse(path, bytes);
                    break;
            }
        }

        private const string MATERIAL_EXTENSION_NAME = "VCAST_vci_material_unity";

        private const string MATERIAL_PATH = "/extensions/" + MATERIAL_EXTENSION_NAME + "/materials";

        public override void ParseJson(string json, IStorage storage)
        {
            base.ParseJson(json, storage);
            var parsed = JsonParser.Parse(Json);
            if (parsed.ContainsKey("extensions")
                && parsed["extensions"].ContainsKey(MATERIAL_EXTENSION_NAME)
                && parsed["extensions"][MATERIAL_EXTENSION_NAME].ContainsKey("materials")
            )
            {
                var nodes = parsed["extensions"][MATERIAL_EXTENSION_NAME]["materials"];
                SetMaterialImporter(new VCIMaterialImporter(this, glTF_VCI_Material.Parse(nodes)));
                SetAnimationImporter(new EachAnimationImporter());
            }
        }

        protected override void OnLoadModel()
        {
            if (GLTF.extensions.VCAST_vci_meta == null)
            {
                base.OnLoadModel();
                return;
            }
        }

        public IEnumerator SetupCorutine()
        {
            VCIObject = Root.AddComponent<VCIObject>();
            yield return ToUnity(meta => VCIObject.Meta = meta);

            // Script
            if (GLTF.extensions.VCAST_vci_embedded_script != null)
                VCIObject.Scripts.AddRange(GLTF.extensions.VCAST_vci_embedded_script.scripts.Select(x =>
                {
                    var source = "";
                    try
                    {
                        var bytes = GLTF.GetViewBytes(x.source);
                        source = Utf8String.Encoding.GetString(bytes.Array, bytes.Offset, bytes.Count);
                    }
                    catch (Exception)
                    {
                        // 握りつぶし
                    }

                    return new VCIObject.Script
                    {
                        name = x.name,
                        mimeType = x.mimeType,
                        targetEngine = x.targetEngine,
                        source = source
                    };
                }));

            // Audio
            if (GLTF.extensions.VCAST_vci_audios != null
                && GLTF.extensions.VCAST_vci_audios.audios != null)
            {
                var root = Root;
                foreach (var audio in GLTF.extensions.VCAST_vci_audios.audios)
                {
#if ((NET_4_6 || NET_STANDARD_2_0) && UNITY_2017_1_OR_NEWER)
                    if (Application.isPlaying)
                    {
                        var bytes = GLTF.GetViewBytes(audio.bufferView);
                        WaveUtil.WaveHeaderData waveHeader;
                        var audioBuffer = new byte[bytes.Count];
                        Buffer.BlockCopy(bytes.Array, bytes.Offset, audioBuffer, 0, audioBuffer.Length);
                        if (WaveUtil.TryReadHeader(audioBuffer, out waveHeader))
                        {
                            AudioClip audioClip = null;
                            yield return AudioClipMaker.Create(
                                audio.name,
                                audioBuffer,
                                44,
                                waveHeader.BitPerSample,
                                waveHeader.DataChunkSize / waveHeader.BlockSize,
                                waveHeader.Channel,
                                waveHeader.SampleRate,
                                false,
                                (clip) => { audioClip = clip; });
                            if (audioClip != null)
                            {
                                var source = root.AddComponent<AudioSource>();
                                source.clip = audioClip;
                                source.playOnAwake = false;
                                source.loop = false;
                            }
                        }
                    }
                    else
#endif
                    {
#if UNITY_EDITOR
                        var audioClip =
                            AssetDatabase.LoadAssetAtPath(AudioAssetPathList[audio.name], typeof(AudioClip)) as
                                AudioClip;
                        if (audioClip != null)
                        {
                            var source = root.AddComponent<AudioSource>();
                            source.clip = audioClip;
                        }
                        else
                        {
                            Debug.LogWarning("AudioFile NotFound: " + audio.name);
                        }
#endif
                    }
                }
            }

            // Animation
            var rootAnimation = Root.GetComponent<Animation>();
            if (rootAnimation != null)
            {
                rootAnimation.playAutomatically = false;
            }

            // SubItem
            for (var i = 0; i < GLTF.nodes.Count; i++)
            {
                var node = GLTF.nodes[i];
                var go = Nodes[i].gameObject;
                if (node.extensions != null)
                {
                    if (node.extensions.VCAST_vci_item != null)
                    {
                        var item = go.AddComponent<VCISubItem>();
                        item.Grabbable = node.extensions.VCAST_vci_item.grabbable;
                        item.Scalable = node.extensions.VCAST_vci_item.scalable;
                        item.UniformScaling = node.extensions.VCAST_vci_item.uniformScaling;
                        item.GroupId = node.extensions.VCAST_vci_item.groupId;
                    }
                }
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="replace">Rootオブジェクトのセットアップを1階層上げるのに使う</param>
        public void SetupPhysics(GameObject rootReplaceTarget = null)
        {
            // Collider and Rigidbody
            for (var i = 0; i < GLTF.nodes.Count; i++)
            {
                var node = GLTF.nodes[i];
                var go = Nodes[i].gameObject;

                if (go == Root && rootReplaceTarget != null) go = rootReplaceTarget;

                if (node.extensions != null)
                {
                    if (node.extensions.VCAST_vci_collider != null
                        && node.extensions.VCAST_vci_collider.colliders != null)
                        foreach (var vciCollider in node.extensions.VCAST_vci_collider.colliders)
                            glTF_VCAST_vci_Collider.AddColliderComponent(go, vciCollider);

                    if (node.extensions.VCAST_vci_rigidbody != null
                        && node.extensions.VCAST_vci_rigidbody.rigidbodies != null)
                        foreach (var rigidbody in node.extensions.VCAST_vci_rigidbody.rigidbodies)
                            glTF_VCAST_vci_Rigidbody.AddRigidbodyComponent(go, rigidbody);
                }
            }

            // Joint
            for (var i = 0; i < GLTF.nodes.Count; i++)
            {
                var node = GLTF.nodes[i];
                var go = Nodes[i].gameObject;
                if (go == Root && rootReplaceTarget != null) go = rootReplaceTarget;

                if (node.extensions != null)
                    if (node.extensions.VCAST_vci_joints != null
                        && node.extensions.VCAST_vci_joints.joints != null)
                        foreach (var joint in node.extensions.VCAST_vci_joints.joints)
                            glTF_VCAST_vci_joint.AddJointComponent(go, joint, Nodes);
            }
        }

        // Attachable
        public void SetupAttachable()
        {
            for (var i = 0; i < GLTF.nodes.Count; i++)
            {
                var node = GLTF.nodes[i];
                var go = Nodes[i].gameObject;

                if (node.extensions != null && node.extensions.VCAST_vci_attachable != null)
                {
                    var attachable = go.AddComponent<VCIAttachable>();
                    attachable.AttachableHumanBodyBones = node.extensions.VCAST_vci_attachable.attachableHumanBodyBones.Select(x =>
                    {
                        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                        {
                            if (x == bone.ToString())
                            {
                                return bone;
                            }
                        }

                        throw new Exception("unknown AttachableHumanBodyBones: " + x);
                    }).ToArray();

                    attachable.AttachableDistance = node.extensions.VCAST_vci_attachable.attachableDistance;
                }
            }
        }

        public void SetupEffekseer()
        {
            // Effekseer
            if (GLTF.extensions != null && GLTF.extensions.Effekseer != null)
            {
                for (var i = 0; i < GLTF.nodes.Count; i++)
                {
                    var node = GLTF.nodes[i];
                    var go = Nodes[i].gameObject;
                    if (node.extensions != null &&
                        node.extensions.Effekseer_emitters != null &&
                        node.extensions.Effekseer_emitters.emitters != null)
                    {
                        foreach (var emitter in node.extensions.Effekseer_emitters.emitters)
                        {
                            var emitterComponent = go.AddComponent<Effekseer.EffekseerEmitter>();
                            var effectIndex = emitter.effectIndex;

                            if (Application.isPlaying)
                            {
                                var effect = GLTF.extensions.Effekseer.effects[effectIndex];
                                var body = GLTF.GetViewBytes(effect.body.bufferView).ToArray();
                                var resourcePath = new Effekseer.EffekseerResourcePath();
                                if (!Effekseer.EffekseerEffectAsset.ReadResourcePath(body, ref resourcePath))
                                {
                                    continue;
                                }

                                // Images
                                var effekseerTextures = new List<Effekseer.Internal.EffekseerTextureResource>();
                                if (effect.images != null && effect.images.Any())
                                {
                                    for (int t = 0; t < effect.images.Count; t++)
                                    {
                                        var image = effect.images[t];
                                        var path = resourcePath.TexturePathList[t];
                                        var buffer = GLTF.GetViewBytes(image.bufferView);

                                        if (image.mimeType == glTF_Effekseer_image.MimeTypeString.Png)
                                        {
                                            Texture2D texture = new Texture2D(2, 2);
                                            texture.LoadImage(buffer.ToArray());
                                            effekseerTextures.Add(new Effekseer.Internal.EffekseerTextureResource()
                                            {
                                                path = path,
                                                texture = texture
                                            });
                                        }
                                        else
                                        {
                                            Debug.LogError(string.Format("image format {0} is not suppported.", image.mimeType));
                                        }
                                    }
                                }


                                // Models
                                var effekseerModels = new List<Effekseer.Internal.EffekseerModelResource>();
                                if (effect.models != null && effect.models.Any())
                                {
                                    for (int t = 0; t < effect.models.Count; t++)
                                    {
                                        var model = effect.models[t];
                                        var path = resourcePath.ModelPathList[t];
                                        path = Path.ChangeExtension(path, "asset");
                                        var buffer = GLTF.GetViewBytes(model.bufferView);

                                        effekseerModels.Add(new Effekseer.Internal.EffekseerModelResource()
                                        {
                                            path = path,
                                            asset = new Effekseer.EffekseerModelAsset() { bytes = buffer.ToArray() }
                                        });
                                    }
                                }

                                Effekseer.EffekseerEffectAsset effectAsset = ScriptableObject.CreateInstance<Effekseer.EffekseerEffectAsset>();
                                effectAsset.name = effect.effectName;
                                effectAsset.efkBytes = body;
                                effectAsset.Scale = effect.scale;
                                effectAsset.textureResources = effekseerTextures.ToArray();
                                effectAsset.modelResources = effekseerModels.ToArray();
                                effectAsset.soundResources = new Effekseer.Internal.EffekseerSoundResource[0];

                                emitterComponent.effectAsset = effectAsset;
                                emitterComponent.playOnStart = emitter.isPlayOnStart;
                                emitterComponent.isLooping = emitter.isLoop;

                                emitterComponent.effectAsset.LoadEffect();
                            }
                            else
                            {
#if UNITY_EDITOR
                                emitterComponent.effectAsset = EffekseerEffectAssets[effectIndex];
                                emitterComponent.playOnStart = emitter.isPlayOnStart;
                                emitterComponent.isLooping = emitter.isLoop;
#endif
                            }

                            EffekseerEmitterComponents.Add(emitterComponent);
                        }
                    }
                }
            }
        }

        public void ExtractAudio(UnityPath prefabPath)
        {
#if UNITY_EDITOR

            if (GLTF.extensions.VCAST_vci_audios == null
                || GLTF.extensions.VCAST_vci_audios.audios == null
                || GLTF.extensions.VCAST_vci_audios.audios.Count == 0)
                return;

            var prefabParentDir = prefabPath.Parent;

            // glb buffer
            var folder = prefabPath.GetAssetFolder(".Audios");

            var created = 0;
            for (var i = 0; i < GLTF.extensions.VCAST_vci_audios.audios.Count; ++i)
            {
                folder.EnsureFolder();
                var audio = GLTF.extensions.VCAST_vci_audios.audios[i];
                var bytes = GLTF.GetViewBytes(audio.bufferView);

                var audioBuffer = new byte[bytes.Count];
                Buffer.BlockCopy(bytes.Array, bytes.Offset, audioBuffer, 0, audioBuffer.Length);

                var path = string.Format("{0}/{1}.wav", folder.Value, audio.name);
                File.WriteAllBytes(path, audioBuffer);
                AudioAssetPathList.Add(audio.name, path);
                created++;
            }

            if (created > 0) AssetDatabase.Refresh();
#endif
        }

        public void ExtractEffekseer(UnityPath prefabPath)
        {
#if UNITY_EDITOR

            if (GLTF.extensions.Effekseer == null
                || GLTF.extensions.Effekseer.effects == null
                || GLTF.extensions.Effekseer.effects.Count == 0)
                return;

            var prefabParentDir = prefabPath.Parent;
            var folder = prefabPath.GetAssetFolder(".Effekseers");

            for (var i = 0; i < GLTF.extensions.Effekseer.effects.Count; ++i)
            {
                folder.EnsureFolder();
                var effect = GLTF.extensions.Effekseer.effects[i];
                var effectDir = string.Format("{0}/{1}", folder.Value, effect.effectName);
                SafeCreateDirectory(effectDir);
                var textureDir = string.Format("{0}/{1}", effectDir, "Textures");
                var modelDir = string.Format("{0}/{1}", effectDir, "Models");

                var body = GLTF.GetViewBytes(effect.body.bufferView).ToArray();
                var resourcePath = new Effekseer.EffekseerResourcePath();
                if (!Effekseer.EffekseerEffectAsset.ReadResourcePath(body, ref resourcePath))
                {
                    continue;
                }

                // Texture
                var writeTexturePathList = new List<string>();
                if (effect.images != null && effect.images.Any())
                {
                    for (int t = 0; t < effect.images.Count; t++)
                    {
                        var image = effect.images[t];
                        var path = resourcePath.TexturePathList[t];
                        var buffer = GLTF.GetViewBytes(image.bufferView);
                        var texturePath = string.Format("{0}/{1}", textureDir, Path.GetFileName(path));
                        SafeCreateDirectory(Path.GetDirectoryName(texturePath));

                        if (image.mimeType == glTF_Effekseer_image.MimeTypeString.Png)
                        {
                            File.WriteAllBytes(texturePath, buffer.ToArray());
                            writeTexturePathList.Add(texturePath);
                        }
                        else
                        {
                            Debug.LogError(string.Format("image format {0} is not suppported.", image.mimeType));
                        }
                    }
                }

                // Models
                if (effect.models != null && effect.models.Any())
                {
                    for (int t = 0; t < effect.models.Count; t++)
                    {
                        var model = effect.models[t];
                        var path = resourcePath.ModelPathList[t];
                        var buffer = GLTF.GetViewBytes(model.bufferView);

                        var modelPath = string.Format("{0}/{1}", modelDir, Path.GetFileName(path));
                        SafeCreateDirectory(Path.GetDirectoryName(modelPath));

                        File.WriteAllBytes(modelPath, buffer.ToArray());
                        AssetDatabase.ImportAsset(modelPath);
                    }
                }

                EditorApplication.delayCall += () =>
                {
                    // fix texture setting
                    foreach (var texturePath in writeTexturePathList)
                    {
                        AssetDatabase.ImportAsset(texturePath);
                        var textureImporter = (TextureImporter)TextureImporter.GetAtPath(texturePath);
                        if(textureImporter != null)
                        {
                            textureImporter.isReadable = true;
                            textureImporter.textureCompression = TextureImporterCompression.Uncompressed;
                            textureImporter.SaveAndReimport();
                        }
                    }

                    // efk
                    var assetPath = string.Format("{0}/{1}.efk", effectDir, effect.effectName);
                    Effekseer.EffekseerEffectAsset.CreateAsset(assetPath, body);
                    var effectAsset = AssetDatabase.LoadAssetAtPath<Effekseer.EffekseerEffectAsset>(System.IO.Path.ChangeExtension(assetPath, "asset"));
                    EffekseerEffectAssets.Add(effectAsset);


                    // find assets
                    // textures
                    for(int t = 0; t < effectAsset.textureResources.Count(); t++)
                    {
                        var path = effectAsset.textureResources[t].path;
                        if (string.IsNullOrEmpty(path))
                            continue;

                        var fileName = Path.GetFileName(path);
                        if (string.IsNullOrEmpty(fileName))
                            continue;

                        Texture2D texture = UnityEditor.AssetDatabase.LoadAssetAtPath(
                            string.Format("{0}/{1}", textureDir, fileName), typeof(Texture2D)) as Texture2D;

                        if(texture != null)
                        {
                            effectAsset.textureResources[t].texture = texture;
                        }
                    }

                    // models
                    for (int t = 0; t < effectAsset.modelResources.Count(); t++)
                    {
                        var path = effectAsset.modelResources[t].path;
                        if (string.IsNullOrEmpty(path))
                            continue;

                        var fileName = Path.GetFileName(path);
                        if (string.IsNullOrEmpty(fileName))
                            continue;

                        Effekseer.EffekseerModelAsset model = UnityEditor.AssetDatabase.LoadAssetAtPath(
                            string.Format("{0}/{1}", modelDir, fileName), typeof(Effekseer.EffekseerModelAsset)) as Effekseer.EffekseerModelAsset;

                        if (model != null)
                        {
                            effectAsset.modelResources[t].asset = model;
                        }
                    }
                };
            }
#endif
        }

        private static DirectoryInfo SafeCreateDirectory(string path)
        {
#if UNITY_EDITOR
            try
            {
                if (Directory.Exists(path))
                {
                    return null;
                }
                var info = Directory.CreateDirectory(path);
                AssetDatabase.ImportAsset(path);
                return info;
            }
            catch(Exception ex)
            {
                throw ex;
            }

#else
            return null;
#endif
        }

        #region Meta

        [Serializable]
        public struct Meta
        {
            public string title;
            public string author;
            public string contactInformation;
            public string reference;
            public Texture2D thumbnail;
            public string version;

            [SerializeField] [TextArea(1, 16)] public string description;

            public string exporterVersion;
            public string specVersion;


            #region Model Data License Url

            [Header("Model Data License")] public glTF_VCAST_vci_meta.LicenseType modelDataLicenseType;
            public string modelDataOtherLicenseUrl;

            #endregion

            #region Script License Url

            [Header("Script License")] public glTF_VCAST_vci_meta.LicenseType scriptLicenseType;
            public string scriptOtherLicenseUrl;

            #endregion

            #region Script Settings

            [Header("Script Settings")] public bool scriptWriteProtected;
            public bool scriptEnableDebugging;

            public glTF_VCAST_vci_meta.ScriptFormat scriptFormat;

            #endregion
        }

        public IEnumerator ToUnity(Action<Meta> callback)
        {
            if (GLTF.extensions.VCAST_vci_meta == null)
            {
                callback(default(Meta));
                yield break;
            }

            var meta = GLTF.extensions.VCAST_vci_meta;
            if (meta == null)
            {
                callback(default(Meta));
                yield break;
            }

            var texture = GetTexture(meta.thumbnail);
            if (texture != null) yield return texture.ProcessCoroutine(GLTF, Storage);

            callback(new Meta
            {
                exporterVersion = meta.exporterVCIVersion,
                specVersion = meta.specVersion,

                title = meta.title,
                version = meta.version,
                author = meta.author,
                contactInformation = meta.contactInformation,
                reference = meta.reference,
                description = meta.description,
                thumbnail = texture != null ? texture.Texture : null,

                modelDataLicenseType = meta.modelDataLicenseType,
                modelDataOtherLicenseUrl = meta.modelDataOtherLicenseUrl,
                scriptLicenseType = meta.scriptLicenseType,
                scriptOtherLicenseUrl = meta.scriptOtherLicenseUrl,

                scriptWriteProtected = meta.scriptWriteProtected,
                scriptEnableDebugging = meta.scriptEnableDebugging,
                scriptFormat = meta.scriptFormat,
            });
        }

        #endregion

        /// <summary>
        /// VCIファイルとして書き出す
        /// </summary>
        /// <param name="path"></param>
        public void Rewrite(string path)
        {
            /* ToDo
            GLTF.extensions.VCAST_vci_audios = null;
            var bytes = GLTF.ToGlbBytes(true);
            File.WriteAllBytes(path, bytes);
            */
        }

        public static VCIImporter CreateForTest()
        {
            var root = new GameObject("root");
            var vciObject = root.AddComponent<VCIObject>();
            var item = new GameObject("item");
            item.AddComponent<VCISubItem>();
            item.transform.SetParent(root.transform);

            var importer = new VCIImporter()
            {
                Root = root,
                VCIObject = vciObject,
            };

            return importer;
        }
    }
}