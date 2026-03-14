using System;
using System.Reflection;
using UnityEngine;
using DaggerfallWorkshop.Game;

namespace RadiantNPCsMod
{
    public class RadiantNPCsInteriorVisitorController : MonoBehaviour
    {
        private const float MoveSpeed = 1.05f;
        private const float TurnSpeed = 240f;
        private const float ArrivalThreshold = 0.08f;
        private const float TalkMinSeconds = 4f;
        private const float TalkMaxSeconds = 8f;

        private RadiantNPCsMain main;
        private MobilePersonNPC npc;
        private Transform focusTransform;
        private Vector3 talkPosition;
        private Vector3 exitPosition;
        private int mapId;
        private int buildingKey;
        private int residentId;
        private float talkUntil = -1f;
        private bool leaving = false;
        private bool encounterVisualActive = false;

        public void Configure(RadiantNPCsMain main, int mapId, int buildingKey, int residentId, MobilePersonNPC npc, Transform focusTransform, Vector3 talkPosition, Vector3 exitPosition)
        {
            this.main = main;
            this.mapId = mapId;
            this.buildingKey = buildingKey;
            this.residentId = residentId;
            this.npc = npc;
            this.focusTransform = focusTransform;
            this.talkPosition = talkPosition;
            this.exitPosition = exitPosition;
            talkUntil = -1f;
            leaving = false;
        }

        private void Update()
        {
            if (GameManager.IsGamePaused || npc == null)
                return;

            if (!leaving)
            {
                if (talkUntil < 0f)
                {
                    if (!MoveToward(talkPosition))
                        return;

                    talkUntil = Time.time + ComputeTalkDuration();
                }

                FaceFocus();
                ApplyEncounterPauseVisual();
                if (Time.time >= talkUntil)
                    leaving = true;
                return;
            }

            if (!MoveToward(exitPosition))
                return;

            if (main != null)
                main.HandleInteriorVisitorDeparture(mapId, buildingKey, residentId, gameObject);
            else
                Destroy(gameObject);
        }

        private bool MoveToward(Vector3 destination)
        {
            Vector3 flatDirection = destination - transform.position;
            flatDirection.y = 0f;
            if (flatDirection.sqrMagnitude <= ArrivalThreshold * ArrivalThreshold)
            {
                transform.position = new Vector3(destination.x, transform.position.y, destination.z);
                return true;
            }

            Quaternion desiredRotation = Quaternion.LookRotation(flatDirection.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);
            float step = MoveSpeed * Time.deltaTime;
            transform.position += transform.forward * Mathf.Min(step, flatDirection.magnitude);
            SetIdle(false);
            return false;
        }

        private void FaceFocus()
        {
            Vector3 focusPosition = focusTransform != null ? focusTransform.position : talkPosition;
            Vector3 lookDirection = focusPosition - transform.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude <= 0.0001f)
                return;

            Quaternion desiredRotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, TurnSpeed * Time.deltaTime);
        }

        private float ComputeTalkDuration()
        {
            unchecked
            {
                uint hash = (uint)(residentId * 1103515245 + buildingKey * 12345);
                float t = (hash & 0xffff) / 65535f;
                return Mathf.Lerp(TalkMinSeconds, TalkMaxSeconds, t);
            }
        }

        private void SetIdle(bool isIdle)
        {
            if (npc != null && npc.Asset != null && npc.Asset.IsIdle != isIdle)
                npc.Asset.IsIdle = isIdle;
        }

        private void ApplyEncounterPauseVisual()
        {
            if (npc == null || npc.Asset == null)
                return;

            if (!encounterVisualActive || npc.Asset.IsIdle)
                npc.Asset.IsIdle = false;

            FreezeDirectionalMovePose();
            encounterVisualActive = true;
        }

        private void FreezeDirectionalMovePose()
        {
            if (npc == null || npc.Asset == null)
                return;

            Type assetType = npc.Asset.GetType();
            object moveAnims = GetPrivateField(assetType, npc.Asset, "moveAnims");
            if (moveAnims != null)
                SetPrivateField(assetType, npc.Asset, "stateAnims", moveAnims);

            SetPrivateField(assetType, npc.Asset, "currentAnimState", 1);
            SetPrivateField(assetType, npc.Asset, "lastOrientation", -1);
            SetPrivateField(assetType, npc.Asset, "currentFrame", 0);
            SetPrivateField(assetType, npc.Asset, "animTimer", 0f);
            SetPrivateField(assetType, npc.Asset, "animSpeed", 0.01f);
            InvokePrivateMethod(assetType, npc.Asset, "UpdateOrientation");
        }

        private static void SetPrivateField(Type type, object instance, string fieldName, object value)
        {
            if (type == null || instance == null)
                return;

            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                if (value != null && field.FieldType.IsEnum && !(value is Enum))
                    value = Enum.ToObject(field.FieldType, value);
                field.SetValue(instance, value);
            }
        }

        private static object GetPrivateField(Type type, object instance, string fieldName)
        {
            if (type == null || instance == null)
                return null;

            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? field.GetValue(instance) : null;
        }

        private static void InvokePrivateMethod(Type type, object instance, string methodName)
        {
            if (type == null || instance == null)
                return;

            MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null)
                method.Invoke(instance, null);
        }
    }
}
