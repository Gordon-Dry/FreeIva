﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FreeIva
{
    /// <summary>
    /// Base class for hatches (points where kerbals can exit or enter a part during IVA).
    /// Manages attachment node, audio, and connections to other hatches
    /// </summary>
    public class FreeIvaHatch : InternalModule
    {
        // ----- fields set in prop config

        [KSPField]
        public string hatchOpenSoundFile = "FreeIva/Sounds/HatchOpen";

        [KSPField]
        public string hatchCloseSoundFile = "FreeIva/Sounds/HatchClose";

        [KSPField]
        public string handleTransformName = string.Empty;

        [KSPField]
        public string doorTransformName = string.Empty;

        [KSPField]
        public string tubeTransformName = string.Empty;

        [KSPField]
        public string cutoutTransformName = string.Empty;

        // ----- the following fields are set via PropHatchConfig, so that they can be different per placement of the prop

        // The name of the part attach node this hatch is positioned on, as defined in the part.cfg's "node definitions".
        // e.g. top => node_stack_top.  Do not include the prefixes (they are stripped out during loading in the stock code).
        public string attachNodeId;
        
        [SerializeReference]
        public ObjectsToHide HideWhenOpen;

        public string airlockName = string.Empty;

        public float tubeExtent = 0;

        public bool hideDoorWhenConnected = false;

        // -----

        [Serializable]
        public struct ObjectToHide
        {
            public string name;
            public Vector3 position;
        }

        // this bullshit brought to you by the Unity serialization system
        public class ObjectsToHide : ScriptableObject
        {
            public List<ObjectToHide> objects = new List<ObjectToHide>();
        }

        Transform m_doorTransform;

        // Where the GameObject is located. Used for basic interaction targeting (i.e. when to show the "Open hatch?" prompt).
        public virtual Vector3 WorldPosition => transform.position;

        private FreeIvaHatch _connectedHatch = null;
        // The other hatch that this one is connected or docked to, if present.
        public FreeIvaHatch ConnectedHatch => _connectedHatch;
        
        public FXGroup HatchOpenSound = null;
        public FXGroup HatchCloseSound = null;

        public bool IsOpen { get; private set; }

        public override void OnLoad(ConfigNode node)
        {
            // is this module placed directly in an INTERNAL node?
            if (internalModel != null)
            {
                Vector3 position = Vector3.zero;
                if (node.TryGetValue("position", ref position))
                {
                    transform.localPosition = position;
                }

                Quaternion rotation = Quaternion.identity;
                if (node.TryGetValue("rotation", ref rotation))
                {
                    transform.localRotation = rotation;
                }
            }
        }
        public override void OnAwake()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;

            Debug.Log($"# Creating hatch {internalProp.propName} for part {part.partInfo.name}");
            
            HatchOpenSound = SetupAudio(hatchOpenSoundFile);
            HatchCloseSound = SetupAudio(hatchCloseSoundFile);

            if (handleTransformName != string.Empty)
            {
                var handleTransform = internalProp.FindModelTransform(handleTransformName);
                if (handleTransform != null)
                {
                    var clickWatcher = handleTransform.gameObject.AddComponent<ClickWatcher>();
                    clickWatcher.AddMouseDownAction(OnHandleClick);
                }
            }

            // if the cutout didn't get removed at load time, do it now
            if (cutoutTransformName != string.Empty)
            {
                var cutoutTransform = internalProp.FindModelTransform(cutoutTransformName);
                if (cutoutTransform != null)
                {
                    GameObject.Destroy(cutoutTransform.gameObject);
                }
            }

            m_doorTransform = internalProp.FindModelTransform(doorTransformName);

            var internalModule = InternalModuleFreeIva.GetForModel(internalModel);
            if (internalModule == null)
            {
                Debug.LogError($"[FreeIva] no InternalModuleFreeIva instance registered for internal {internalModel.internalName} for hatch prop {internalProp.propName}");
                return;
            }
            internalModule.Hatches.Add(this);

            // start a coroutine so that all the hatches have been initialized
            StartCoroutine(CheckForConnection());
        }

        void SetTubeScale()
        {
            // scale tube appropriately
            var tubeTransform = internalProp.FindModelTransform(tubeTransformName);
            if (tubeTransform != null)
            {
                float tubeScale = 0;

                // if we're connected to another hatch, find the midpoint between our attach nodes - this is for passthrough
                // for parts that are directly connected to each other, the attach nodes are in the same place
                // but with passthrough, we need to find a point to meet
                if (_connectedHatch != null)
                {
                    var otherTubeTransform = _connectedHatch.internalProp.FindModelTransform(_connectedHatch.tubeTransformName);

                    // if the other transform doesn't have a tube, then we scale ours to reach the other prop's origin
                    if (otherTubeTransform == null)
                    {
                        tubeScale = Vector3.Distance(tubeTransform.position, _connectedHatch.transform.position);
                    }
                    else
                    {
                        // otherwise we find the midpoint
                        tubeScale = Vector3.Distance(tubeTransform.position, otherTubeTransform.position) * 0.5f;
                    }
                }
                else
                {
                    // try to determine tube length from attach node
                    var myAttachNode = part.FindAttachNode(attachNodeId);
                    if (tubeExtent == 0 && myAttachNode != null)
                    {
                        tubeExtent = Vector3.Dot(myAttachNode.originalPosition, myAttachNode.originalOrientation);
                    }

                    if (tubeExtent != 0)
                    {
                        Vector3 tubePositionInIVA = internalModel.transform.InverseTransformPoint(tubeTransform.position);
                        Vector3 tubeDownVectorWorld = tubeTransform.rotation * Vector3.down;
                        Vector3 tubeDownVectorIVA = internalModel.transform.InverseTransformVector(tubeDownVectorWorld);

                        float tubePositionOnAxis = Vector3.Dot(tubeDownVectorIVA, tubePositionInIVA);
                        tubeScale = tubeExtent - tubePositionOnAxis;

                        // TODO: what if the prop itself is scaled?
                    }
                }

                if (tubeScale <= 0)
                {
                    GameObject.Destroy(tubeTransform.gameObject);
                }
                else
                {
                    tubeTransform.localScale = new Vector3(1.0f, tubeScale, 1.0f);
                }
            }
        }

        IEnumerator CheckForConnection()
        {
            yield return null;

            if (_connectedHatch == null)
            {
                _connectedHatch = FindConnectedHatch();
            }

            if (_connectedHatch != null)
            {
                _connectedHatch._connectedHatch = this;
            }

            if (_connectedHatch != null && hideDoorWhenConnected)
            {
                Open(true);
                HideOnOpen(true, true);
                _connectedHatch.HideOnOpen(true, true);
                if (m_doorTransform != null)
                {
                    // right now this is redundant, but eventually doors will animate open instead of disappearing
                    m_doorTransform.gameObject.SetActive(false);
                }
                if (_connectedHatch.m_doorTransform != null)
                {
                    _connectedHatch.m_doorTransform.gameObject.SetActive(false);
                }

                enabled = false;
                _connectedHatch.enabled = false;
            }

            SetTubeScale();
        }

        private void OnHandleClick()
        {
            ToggleHatch();
        }


        private FreeIvaHatch FindConnectedHatch()
        {
            AttachNode currentNode = part.FindAttachNode(attachNodeId);
            if (currentNode == null) return null;

            // these variables are set in the below loop
            AttachNode otherNode = null;
            InternalModuleFreeIva otherIvaModule = null;

            // find the Iva module and attachnode that this hatch connects to, possibly through a chain of passthrough parts
            Part currentPart = part;
            Part otherPart = currentNode.attachedPart;
            while (otherPart != null)
            {
                // note: FindOpposingNode doesn't seem to work because currentNode.owner seems to be null
                otherNode = otherPart.FindAttachNodeByPart(currentPart);

                // if we found an internal module, we're done
                otherIvaModule = InternalModuleFreeIva.GetForModel(otherPart.internalModel);
                if (otherIvaModule != null) break;

                // if there's a part module, see if it supports passthrough for this node
                var ivaPartModule = otherPart.GetModule<ModuleFreeIva>();
                if (ivaPartModule == null) return null;

                string passThroughNodeName = null;
                if (otherNode.id == ivaPartModule.passThroughNodeA)
                {
                    passThroughNodeName = ivaPartModule.passThroughNodeB;
                }
                else if (otherNode.id == ivaPartModule.passThroughNodeB)
                {
                    passThroughNodeName = ivaPartModule.passThroughNodeA;
                }
                else
                {
                    // no passthrough; we're done
                    return null;
                }

                // get the node on the far side of the other part
                currentNode = otherPart.FindAttachNode(passThroughNodeName);

                if (currentNode == null)
                {
                    Debug.LogError($"[FreeIva] node {passThroughNodeName} wasn't found in part {otherPart.partInfo.name}");
                    return null;
                }

                currentPart = otherPart;
                otherPart = currentNode.attachedPart;
            }

            if (otherIvaModule == null) return null;

            // look for a hatch that is on the node we're connected to
            foreach (var otherHatch in otherIvaModule.Hatches)
            {
                if (otherHatch.attachNodeId == otherNode.id)
                {
                    return otherHatch;
                }
            }

            return null;
        }

        public FXGroup SetupAudio(string soundFile)
        {
            FXGroup result = null;
            if (!string.IsNullOrEmpty(soundFile))
            {
                result = new FXGroup("HatchOpen");
                result.audio = gameObject.AddComponent<AudioSource>(); // TODO: if we deactivate this object when the hatch opens, do we need to put the sound on a different object?
                result.audio.dopplerLevel = 0f;
                result.audio.Stop();
                result.audio.clip = GameDatabase.Instance.GetAudioClip(hatchOpenSoundFile);
                result.audio.loop = false;
            }

            return result;
        }

        public void ToggleHatch()
        {
            Open(!IsOpen);
        }

        public virtual void Open(bool open)
        {
            var connectedHatch = ConnectedHatch;

            // if we're trying to open a door to space, just go EVA
            if (connectedHatch == null && open)
            {
                GoEVA();
            }
            else
            {
                if (m_doorTransform != null)
                {
                    m_doorTransform.gameObject.SetActive(!open);
                }

                HideOnOpen(open, false);

                if (open != IsOpen)
                {
                    var sound = open ? HatchOpenSound : HatchCloseSound;
                    if (sound != null && sound.audio != null)
                        sound.audio.Play();
                }

                IsOpen = open;

                // automatically toggle the far hatch too
                if (connectedHatch != null && connectedHatch.IsOpen != open)
                {
                    connectedHatch.Open(open);
                    FreeIva.SetRenderQueues(FreeIva.CurrentPart);
                }
            }
        }

        public virtual void HideOnOpen(bool open, bool permanent)
        {
            if (HideWhenOpen == null) return;

            MeshRenderer[] meshRenderers = internalModel.GetComponentsInChildren<MeshRenderer>();
            foreach (var hideProp in HideWhenOpen.objects)
            {
                bool found = false;

                foreach (MeshRenderer mr in meshRenderers)
                {
                    if (mr.name.Equals(hideProp.name) && mr.transform != null)
                    {
                        float error = Vector3.Distance(mr.transform.localPosition, hideProp.position);
                        if (error < 0.15)
                        {
                            Debug.Log("# Toggling " + mr.name);

                            if (permanent)
                            {
                                GameObject.Destroy(mr.gameObject);
                            }
                            else
                            {
                                mr.gameObject.SetActive(!open);
                            }
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    Debug.LogError($"[FreeIva] HideWhenOpen - could not find meshrenderer named {hideProp.name} in model {internalModel.internalName}");
                }
            }
        }

        static Transform FindAirlock(Part part, string airlockName)
        {
            if (!string.IsNullOrEmpty(airlockName))
            {
                var childTransform = part.FindModelTransform(airlockName);

                if (childTransform != null && childTransform.CompareTag("Airlock"))
                {
                    return childTransform;
                }
                else if (childTransform == null)
                {
                    Debug.LogError($"[FreeIva] could not find airlock transform named {airlockName} on part {part.partInfo.name}");
                }
                else
                {
                    Debug.LogError($"[FreeIva] found airlock transform named {airlockName} on part {part.partInfo.name} but it doesn't have the 'Airlock' tag");
                }
            }

            return part.airlock;
        }

        bool GoEVA()
        {
            float acLevel = ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex);
            bool evaUnlocked = GameVariables.Instance.UnlockedEVA(acLevel);
            bool evaPossible = GameVariables.Instance.EVAIsPossible(evaUnlocked, vessel);

            Kerbal kerbal = CameraManager.Instance.IVACameraActiveKerbal;

            if (kerbal != null && evaPossible && HighLogic.CurrentGame.Parameters.Flight.CanEVA)
            {
                // This isn't correct if you're trying to EVA from a hatch that isn't on the part you had been sitting in
                // not sure what the best solution is; maybe move the kerbal to this part?  But what if the EVA fails?
                var kerbalEVA = FlightEVA.fetch.spawnEVA(kerbal.protoCrewMember, kerbal.InPart, FindAirlock(kerbal.InPart, airlockName), true);

                if (kerbalEVA != null)
                {
                    CameraManager.Instance.SetCameraFlight();
                    return true;
                }
            }

            return false;
        }

        public static void InitialiseAllHatchesClosed()
        {
            foreach (Part p in FlightGlobals.ActiveVessel.Parts)
            {
                InternalModuleFreeIva mfi = InternalModuleFreeIva.GetForModel(p.internalModel);
                if (mfi != null)
                {
                    foreach (var hatch in mfi.Hatches)
                    {
                        hatch.Open(false);
                    }
                }
            }
        }
    }
}
