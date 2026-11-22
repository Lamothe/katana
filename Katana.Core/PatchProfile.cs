namespace Katana.Core;

// Preserve every byte in the original documented range, including parameters not exposed in the UI.
// This is not a claim of complete coverage of all Artist firmware extensions.
public static class PatchProfile
{
    public const string Id = "original-2327-v1";
    public const int Length = 2327;
    public const int BufferAddress = 0x60 << 21;
    public static int StoredSlotAddress(int slot)
    {
        ValidateSlot(slot, false);
        return (0x10 << 21) | (slot << 14);
    }

    public static void ValidateSlot(int slot, bool allowPanel)
    {
        if (slot < (allowPanel ? 0 : 1) || slot > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), allowPanel ? "Choose Panel or a slot from A1 to B4." : "Choose a slot from A1 to B4; Panel cannot be overwritten.");
        }
    }

    public static string SlotLabel(int slot) => slot == 0 ? "Panel" : $"{(slot <= 4 ? "A" : "B")}{(slot - 1) % 4 + 1}";

    public const int SlotAddress = (0x10 << 21) | (1 << 14);
}
