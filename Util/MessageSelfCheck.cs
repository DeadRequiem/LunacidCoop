using System;
using System.Reflection;

namespace LunacidCoopMod
{
        // Runs once at start; a missing Deserialize arm fails here, not silently on the far machine.
    public static class MessageSelfCheck
    {
        public static void Run()
        {
            var baseType = typeof(NetworkMessage);
            int checked_ = 0, broken = 0;

            CoopLog.Suppress = true;
            try
            {

                foreach (var type in baseType.Assembly.GetTypes())
                {
                    if (type.IsAbstract || !baseType.IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        Plugin.Log.LogWarning($"[MsgCheck] {type.Name} has no parameterless constructor; cannot verify");
                        continue;
                    }

                    NetworkMessage probe;
                    try { probe = (NetworkMessage)Activator.CreateInstance(type); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[MsgCheck] {type.Name} could not be constructed: {e.Message}");
                        continue;
                    }

                    checked_++;
                try
                    {
                        // Serialize prepends a 4-byte length; Deserialize takes the body alone.
                        byte[] packet = NetworkMessageHandler.Serialize(probe);
                        byte[] body = new byte[packet.Length - 4];
                        Array.Copy(packet, 4, body, 0, body.Length);

                        if (NetworkMessageHandler.Deserialize(body) == null)
                        {
                            broken++;
                            Plugin.Log.LogError($"[MsgCheck] {type.Name} has no Deserialize case - peers will DROP it");
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        broken++;
                        Plugin.Log.LogError($"[MsgCheck] {type.Name} failed its round trip: {e.Message}");
                        continue;
                    }

                    Plugin.Log.LogDebug($"[MsgCheck] {type.Name}: reliable={MessagePump.IsReliable(probe)} " +
                                        $"relayed={MessagePump.IsRelayable(probe)}");
                }

            }
            finally { CoopLog.Suppress = false; }

            if (broken > 0)
                Plugin.Log.LogError($"[MsgCheck] {broken} of {checked_} message types are not wired up correctly");
            else
                Plugin.Log.LogInfo($"[MsgCheck] {checked_} message types round-trip cleanly");
        }
    }
}
