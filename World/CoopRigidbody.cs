using System.Collections.Generic;
using UnityEngine;

namespace LunacidCoopMod
{
    // Marker on every networked rigidbody. Closest player owns it and streams
    // state; hysteresis + brief post-apply cooldown prevent ownership ping-pong.
    public class CoopRigidbody : MonoBehaviour
    {
        private const float TAKE_RADIUS    = 2f;
        private const float RELEASE_RADIUS = 4f;
        private const float BROADCAST_HZ   = 10f;
        private const float APPLY_COOLDOWN = 0.25f;

        public string Identifier;

        private static readonly Dictionary<string, CoopRigidbody> _active = new Dictionary<string, CoopRigidbody>();

        private Rigidbody _rb;
        private bool _isOwner;
        private float _lastBroadcast;
        private float _lastRemoteApply;

        public static CoopRigidbody Register(GameObject go, string id)
        {
            if (go == null || string.IsNullOrEmpty(id)) return null;

            var existing = go.GetComponent<CoopRigidbody>();
            if (existing != null) return existing;

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Plugin.Log.LogWarning($"[CoopRigidbody] Register('{id}') skipped - no Rigidbody on {go.name}");
                return null;
            }

            var c = go.AddComponent<CoopRigidbody>();
            c.Identifier = id;
            c._rb = rb;
            _active[id] = c;
            CoopLog.CoopRigidbody($"Registered '{id}' on {go.name} (kinematic={rb.isKinematic})");
            return c;
        }

        public static CoopRigidbody Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            _active.TryGetValue(id, out var c);
            return c;
        }

        public static void ClearAll()
        {
            _active.Clear();
        }

        public void ApplyRemoteState(RigidbodyStateMessage m)
        {
            if (_rb == null) return;

            // transform.position applies immediately and syncs back; Rigidbody.position queues to next physics step.
            transform.position = m.Position;
            transform.rotation = Quaternion.Euler(m.Rotation);
            if (!_rb.isKinematic)
            {
                _rb.velocity        = m.Velocity;
                _rb.angularVelocity = m.AngularVelocity;
            }
            _lastRemoteApply = Time.time;
        }

        private void Update()
        {
            if (_rb == null) return;
            if (!SyncHandler.IsConnected) return;

            // Only consider real, local players for ownership & never dummies.
            // GetNearestReal returns null in single-player; everything stays unowned.
            var localRef = PlayerRegistry.GetNearestReal(transform.position);
            if (localRef == null) return;

            float distSqr = (transform.position - localRef.position).sqrMagnitude;

            if (_isOwner)
            {
                if (distSqr > RELEASE_RADIUS * RELEASE_RADIUS)
                    _isOwner = false;
            }
            else
            {
                if (distSqr < TAKE_RADIUS * TAKE_RADIUS)
                    _isOwner = true;
            }

            if (!_isOwner) return;

            // Don't broadcast immediately after a remote apply; prevents ownership ping-pong.
            if (Time.time - _lastRemoteApply < APPLY_COOLDOWN) return;

            float interval = 1f / BROADCAST_HZ;
            if (Time.time - _lastBroadcast < interval) return;

            SyncHandler.Send(new RigidbodyStateMessage
            {
                Identifier      = Identifier,
                Position        = _rb.position,
                Rotation        = _rb.rotation.eulerAngles,
                Velocity        = _rb.velocity,
                AngularVelocity = _rb.angularVelocity
            });
            _lastBroadcast = Time.time;
        }

        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(Identifier)) _active.Remove(Identifier);
        }
    }
}
