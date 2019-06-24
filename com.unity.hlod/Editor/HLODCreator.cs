﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using System.Linq;
using Unity.Collections;
using Unity.HLODSystem.Simplifier;
using Unity.HLODSystem.SpaceManager;
using Unity.HLODSystem.Streaming;
using Unity.HLODSystem.Utils;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Unity.HLODSystem
{
    static class HLODCreator
    {
        private static List<MeshRenderer> GetMeshRenderers(List<GameObject> gameObjects, float thresholdSize)
        {
            List<MeshRenderer> meshRenderers = new List<MeshRenderer>();

            for (int i = 0; i < gameObjects.Count; ++i)
            {
                GameObject obj = gameObjects[i];
                LODGroup lodGroup = obj.GetComponent<LODGroup>();

                Renderer[] renderers;

                if (lodGroup != null)
                {
                    renderers = lodGroup.GetLODs().Last().renderers;
                }
                else
                {
                    renderers = obj.GetComponents<Renderer>();
                }

                for (int ri = 0; ri < renderers.Length; ++ri)
                {
                    MeshRenderer mr = renderers[ri] as MeshRenderer;

                    if (mr == null)
                        continue;

                    float max = Mathf.Max(mr.bounds.size.x, mr.bounds.size.y, mr.bounds.size.z);
                    if (max < thresholdSize)
                        continue;

                    meshRenderers.Add(mr);
                }
            }

            return meshRenderers;
        }

        private static DisposableList<HLODBuildInfo> CreateBuildInfo(SpaceNode root, float thresholdSize)
        {
            DisposableList<HLODBuildInfo> results = new DisposableList<HLODBuildInfo>();
            Queue<SpaceNode> trevelQueue = new Queue<SpaceNode>();
            Queue<int> parentQueue = new Queue<int>();
            Queue<string> nameQueue = new Queue<string>();

            trevelQueue.Enqueue(root);
            parentQueue.Enqueue(-1);
            nameQueue.Enqueue("");
            

            while (trevelQueue.Count > 0)
            {
                int currentNodeIndex = results.Count;
                string name = nameQueue.Dequeue();
                SpaceNode node = trevelQueue.Dequeue();
                HLODBuildInfo info = new HLODBuildInfo
                {
                    Name = name,
                    ParentIndex = parentQueue.Dequeue(),
                    Target = node
                };


                for (int i = 0; i < node.GetChildCount(); ++i)
                {
                    trevelQueue.Enqueue(node.GetChild(i));
                    parentQueue.Enqueue(currentNodeIndex);
                    nameQueue.Enqueue(name + "_" + (i + 1));
                }
                

                results.Add(info);

                //it should add to every parent.
                List<MeshRenderer> meshRenderers = GetMeshRenderers(node.Objects, thresholdSize);
                int distance = 0;

                while (currentNodeIndex >= 0)
                {
                    var curInfo = results[currentNodeIndex];

                    for (int i = 0; i < meshRenderers.Count; ++i) 
                    {
                        curInfo.WorkingObjects.Add(meshRenderers[i].ToWorkingObject(Allocator.Persistent));
                        curInfo.Distances.Add(distance);
                    }

                    currentNodeIndex = curInfo.ParentIndex;
                    distance += 1;
                }

            }

            return results;
        }

        public static IEnumerator Create(HLOD hlod)
        {
            try
            {


                Stopwatch sw = new Stopwatch();

                AssetDatabase.Refresh();
                AssetDatabase.SaveAssets();

                sw.Reset();
                sw.Start();

                Bounds bounds = hlod.GetBounds();

                List<GameObject> hlodTargets = ObjectUtils.HLODTargets(hlod.gameObject);
                ISpaceSplitter spliter = new QuadTreeSpaceSplitter(hlod.transform.position, 5.0f, hlod.MinSize);
                SpaceNode rootNode = spliter.CreateSpaceTree(bounds, hlodTargets, progress =>
                {
                    EditorUtility.DisplayProgressBar("Bake HLOD", "Splitting space", progress * 0.25f);
                });


                using (DisposableList<HLODBuildInfo> buildInfos = CreateBuildInfo(rootNode, hlod.ThresholdSize))
                {

                    try
                    {
                        Debug.Log("[HLOD] Splite space: " + sw.Elapsed.ToString("g"));
                        sw.Reset();
                        sw.Start();

                        ISimplifier simplifier = (ISimplifier) Activator.CreateInstance(hlod.SimplifierType,
                            new object[] {hlod.SimplifierOptions});
                        for (int i = 0; i < buildInfos.Count; ++i)
                        {
                            yield return new BranchCoroutine(simplifier.Simplify(buildInfos[i]));
                        }

                        yield return new WaitForBranches(progress =>
                        {
                            EditorUtility.DisplayProgressBar("Bake HLOD", "Simplify meshes",
                                0.25f + progress * 0.25f);
                        });
                        Debug.Log("[HLOD] Simplify: " + sw.Elapsed.ToString("g"));
                        sw.Reset();
                        sw.Start();


                        IBatcher batcher =
                            (IBatcher) Activator.CreateInstance(hlod.BatcherType, new object[] {hlod.BatcherOptions});
                        batcher.Batch(hlod.transform.position, buildInfos,
                            progress =>
                            {
                                EditorUtility.DisplayProgressBar("Bake HLOD", "Generating combined static meshes.",
                                    0.5f + progress * 0.25f);
                            });
                        Debug.Log("[HLOD] Batch: " + sw.Elapsed.ToString("g"));
                        sw.Reset();
                        sw.Start();


                        //AssetDatabase.StartAssetEditing();
                        IStreamingBuilder builder =
                            (IStreamingBuilder) Activator.CreateInstance(hlod.StreamingType, new object[] { hlod.StreamingOptions});
                        builder.Build(rootNode, buildInfos, hlod.gameObject, hlod.CullDistance, hlod.LODDistance, 
                            progress =>
                            {
                                EditorUtility.DisplayProgressBar("Bake HLOD", "Storing results.",
                                    0.75f + progress * 0.25f);
                            });
                        Debug.Log("[HLOD] Build: " + sw.Elapsed.ToString("g"));
                        sw.Reset();
                        sw.Start();
                    }
                    finally
                    {

                        //AssetDatabase.StopAssetEditing();
                        Debug.Log("[HLOD] Importing: " + sw.Elapsed.ToString("g"));
                    }
                }

            }
            finally
            {
                EditorUtility.ClearProgressBar();
                
            }
            //hlod.Root = rootNode;
        }

        public static IEnumerator Destroy(HLOD hlod)
        {

            var controller = hlod.GetComponent<ControllerBase>();
            if (controller == null)
                yield break;

            try
            {
                EditorUtility.DisplayProgressBar("Destory HLOD", "Destrying HLOD files", 0.0f);
                AssetDatabase.StartAssetEditing();

                var generatedObjects = hlod.GeneratedObjects;
                for (int i = 0; i < generatedObjects.Count; ++i)
                {
                    if (generatedObjects[i] == null)
                        continue;
                    var path = AssetDatabase.GetAssetPath(generatedObjects[i]);
                    if (string.IsNullOrEmpty(path) == false)
                    {
                        AssetDatabase.DeleteAsset(path);
                    }

                    EditorUtility.DisplayProgressBar("Destory HLOD", "Destrying HLOD files", (float)i / (float)generatedObjects.Count);
                }
                generatedObjects.Clear();

                Object.DestroyImmediate(controller);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }
        }

    }
}
