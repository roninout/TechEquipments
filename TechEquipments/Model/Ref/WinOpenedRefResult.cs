namespace TechEquipments
{
    /// <summary>
    /// Результат поиска ссылки WinOpened.
    /// RefEquip = значение поля REFEQUIP (то, что тебе нужно).
    /// Assoc    = значение поля ASSOC (просил читать вместо CUSTOM).
    /// </summary>
    public sealed record WinOpenedRefResult(string RefEquip, string Assoc);
}
