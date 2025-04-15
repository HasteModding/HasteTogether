using System.Text;
using Steamworks;
using UnityEngine;

namespace HasteTogether;

public abstract class Packet
{
    public abstract byte PacketID();
    public abstract byte[] Serialize();

    // Helper to map internal enum to Steamworks enum
    protected EP2PSend MapSteamSendType(SendType sendType)
    {
        switch (sendType)
        {
            case SendType.Reliable:
                return EP2PSend.k_EP2PSendReliable;
            case SendType.Unreliable:
            default:
                return EP2PSend.k_EP2PSendUnreliableNoDelay; // Good default for game data
        }
    }

    // Prepares the full byte array with Packet ID prefix
    protected byte[] GetBytesToSend()
    {
        byte[] data = Serialize();
        byte[] toSend = new byte[data.Length + 1];
        toSend[0] = PacketID(); // Add packet ID header
        Buffer.BlockCopy(data, 0, toSend, 1, data.Length);
        return toSend;
    }

    public void Broadcast(SendType sendType = SendType.Unreliable)
    {
        if (Plugin.networkManager == null || !Plugin.networkManager.IsInLobby)
            return;

        byte[] toSend = GetBytesToSend();
        Plugin.networkManager.Broadcast(toSend, sendType); // Use NetworkManager's broadcast
    }

    public void SendTo(CSteamID targetSteamId, SendType sendType = SendType.Unreliable) // Use CSteamID
    {
        if (Plugin.networkManager == null || !Plugin.networkManager.IsInLobby)
            return;

        byte[] toSend = GetBytesToSend();
        Plugin.networkManager.SendTo(targetSteamId, toSend, sendType); // Use NetworkManager's SendTo
    }
}

public class UpdatePacket : Packet
{
    public override byte PacketID() => 0x01;

    private Vector3 position;
    private Quaternion rotation;
    private bool isGrounded; // Added field

    // Serialization needs to include the grounded state
    public override byte[] Serialize()
    {
        byte[] transformData = Plugin.SerializeTransform(position, rotation); // 15 bytes
        byte[] packetData = new byte[16]; // 15 bytes transform + 1 byte grounded
        Buffer.BlockCopy(transformData, 0, packetData, 0, 15);
        packetData[15] = (byte)(isGrounded ? 1 : 0); // Add grounded state
        return packetData;
    }

    // Need a way to deserialize this on the receiving end if needed directly from packet
    // Or just pass the raw transform and grounded byte separately in ProcessPacketData
    public static void Deserialize(byte[] data, out Vector3 pos, out Quaternion rot, out bool grounded)
    {
        if (data == null || data.Length < 16) throw new Exception("Invalid UpdatePacket data");

        byte[] transformData = new byte[15];
        Buffer.BlockCopy(data, 0, transformData, 0, 15);

        // Reuse logic from NetworkedPlayer.ApplyTransform for position/rotation
        int xR = (transformData[0] << 16) | (transformData[1] << 8) | transformData[2];
        int yR = (transformData[3] << 16) | (transformData[4] << 8) | transformData[5];
        int zR = (transformData[6] << 16) | (transformData[7] << 8) | transformData[8];
        pos = new Vector3(
            (xR / 256.0f) - 32767.5f,
            (yR / 256.0f) - 32767.5f,
            (zR / 256.0f) - 32767.5f
        );
        int rY = (transformData[9] << 16) | (transformData[10] << 8) | transformData[11];
        int rW = (transformData[12] << 16) | (transformData[13] << 8) | transformData[14];
        float rotY = (rY / 8388607.5f) - 1.0f;
        float rotW = (rW / 8388607.5f) - 1.0f;
        float y2 = rotY * rotY; float w2 = rotW * rotW;
        float xzSumSq = 1.0f - y2 - w2;
        float rotX = 0.0f; float rotZ = (xzSumSq < 0.0f) ? 0.0f : Mathf.Sqrt(xzSumSq);
        rot = new Quaternion(rotX, rotY, rotZ, rotW).normalized;

        grounded = data[15] != 0; // Get grounded state
    }


    // Comparison logic should also check grounded state
    public bool HasChanged(UpdatePacket previousPacket, float posThresholdSqr = 0.0001f, float rotThreshold = 0.1f)
    {
        if (previousPacket == null) return true;
        if (isGrounded != previousPacket.isGrounded) return true; // Check grounded state
        if ((position - previousPacket.position).sqrMagnitude > posThresholdSqr) return true;
        if (Quaternion.Angle(rotation, previousPacket.rotation) > rotThreshold) return true;
        return false;
    }

    // Constructor now takes grounded state
    public UpdatePacket(Transform player, bool isGrounded)
    {
        this.position = player.position;
        var visualRotation = player.GetComponent<PlayerVisualRotation>();
        this.rotation = visualRotation != null ? visualRotation.visual.rotation : player.rotation;
        this.isGrounded = isGrounded;
    }

    // Default constructor
    public UpdatePacket()
    {
        this.position = Vector3.zero;
        this.rotation = Quaternion.identity;
        this.isGrounded = false;
    }
}
public class NamePacket : Packet
{
    private string name;

    public override byte PacketID() => 0x02; // ID is 0x02

    // Serialization uses UTF8 encoding, which is standard
    public override byte[] Serialize() => Encoding.UTF8.GetBytes(name);

    // Constructor ensures name is not null
    public NamePacket(string name)
    {
        this.name = name ?? "UnknownPlayer"; // Provide a default if null
    }
}

// Simple event, no extra data needed
public class JumpPacket : Packet
{
    public override byte PacketID() => 0x10;
    public override byte[] Serialize() => new byte[0];
}

// Includes LandingType enum and a boolean
public enum LandingType
{
	Bad,
	Ok,
	Good,
	Perfect,
	None
}

public class LandPacket : Packet
{
    public LandingType LandingType;
    public bool SavedLanding;
    public override byte PacketID() => 0x11;
    public override byte[] Serialize() => new byte[] { (byte)LandingType, (byte)(SavedLanding ? 1 : 0) };

    public static LandPacket Deserialize(byte[] data)
    {
        if (data == null || data.Length < 2) throw new Exception("Invalid LandPacket data");
        return new LandPacket { LandingType = (LandingType)data[0], SavedLanding = data[1] != 0 };
    }
}

// Simple event
public class WallBouncePacket : Packet
{
    public override byte PacketID() => 0x12;
    public override byte[] Serialize() => new byte[0];
}

// Simple event
public class WavePacket : Packet
{
    public override byte PacketID() => 0x13;
    public override byte[] Serialize() => new byte[0];
}

// Includes damage direction float
public class TakeDamagePacket : Packet
{
    public float DamageDirectionValue; // Value from -1 to 1
    public override byte PacketID() => 0x14;
    public override byte[] Serialize() => BitConverter.GetBytes(DamageDirectionValue);

    public static TakeDamagePacket Deserialize(byte[] data)
    {
        if (data == null || data.Length < 4) throw new Exception("Invalid TakeDamagePacket data");
        return new TakeDamagePacket { DamageDirectionValue = BitConverter.ToSingle(data, 0) };
    }
}

// Includes animation ID integer
public class SetShardAnimPacket : Packet
{
    public int AnimationId;
    public override byte PacketID() => 0x15;
    public override byte[] Serialize() => BitConverter.GetBytes(AnimationId);

    public static SetShardAnimPacket Deserialize(byte[] data)
    {
        if (data == null || data.Length < 4) throw new Exception("Invalid SetShardAnimPacket data");
        return new SetShardAnimPacket { AnimationId = BitConverter.ToInt32(data, 0) };
    }
}

// Includes confidence float
public class SetConfidencePacket : Packet
{
    public float ConfidenceValue;
    public override byte PacketID() => 0x16;
    public override byte[] Serialize() => BitConverter.GetBytes(ConfidenceValue);

    public static SetConfidencePacket Deserialize(byte[] data)
    {
        if (data == null || data.Length < 4) throw new Exception("Invalid SetConfidencePacket data");
        return new SetConfidencePacket { ConfidenceValue = BitConverter.ToSingle(data, 0) };
    }
}

// Includes animation name string
public class PlayAnimationPacket : Packet
{
    public string? AnimationName;
    public override byte PacketID() => 0x17;
    public override byte[] Serialize() => Encoding.UTF8.GetBytes(AnimationName ?? "");

    public static PlayAnimationPacket Deserialize(byte[] data)
    {
        if (data == null) throw new Exception("Invalid PlayAnimationPacket data");
        return new PlayAnimationPacket { AnimationName = Encoding.UTF8.GetString(data) };
    }
}

// Includes grapple state bool and vector
public class GrappleStatePacket : Packet
{
    public bool IsGrappling;
    public Vector3 GrappleVector;
    public override byte PacketID() => 0x18;

    public override byte[] Serialize()
    {
        // 1 byte bool + 3 * 4 bytes for Vector3 = 13 bytes
        byte[] data = new byte[13];
        data[0] = (byte)(IsGrappling ? 1 : 0);
        Buffer.BlockCopy(BitConverter.GetBytes(GrappleVector.x), 0, data, 1, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(GrappleVector.y), 0, data, 5, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(GrappleVector.z), 0, data, 9, 4);
        return data;
    }

    public static GrappleStatePacket Deserialize(byte[] data)
    {
        if (data == null || data.Length < 13) throw new Exception("Invalid GrappleStatePacket data");
        return new GrappleStatePacket
        {
            IsGrappling = data[0] != 0,
            GrappleVector = new Vector3(
                BitConverter.ToSingle(data, 1),
                BitConverter.ToSingle(data, 5),
                BitConverter.ToSingle(data, 9)
            )
        };
    }
}
