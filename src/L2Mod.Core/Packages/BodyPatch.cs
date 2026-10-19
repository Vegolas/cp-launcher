namespace L2Mod.Core.Packages;

/// <summary>Size-preserving replacement of bytes at an offset of the decrypted package body.</summary>
public sealed record BodyPatch(int BodyOffset, byte[] Bytes);
