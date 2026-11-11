namespace WechatDraft.Core;

public static class DemoContactDefaults
{
    private const string DefaultName = "小明";

    public static List<Contact> Load(LocalRepository repository, string scope)
    {
        var contacts = repository.Contacts(scope).Where(contact => contact.IsDemo && contact.Confirmed).ToList();
        var legacy = contacts.Where(IsLegacyDefault).ToArray();
        if (legacy.Length == 0 && contacts.Count > 0) return contacts;

        var current = contacts.Except(legacy).ToList();
        string? renamedId = null;
        if (!current.Any(contact => contact.DisplayName == DefaultName))
        {
            var oldFriend = legacy.FirstOrDefault(contact => contact.DisplayName == "小林");
            var replacement = (oldFriend ?? new Contact
            {
                AccountScope = scope, Relationship = "friend", Confirmed = true, IsDemo = true, BindingStatus = "demo"
            }) with { DisplayName = DefaultName, RemarkName = DefaultName, AddressTerm = DefaultName };
            repository.SaveContact(replacement);
            renamedId = oldFriend?.Id;
            current.Insert(0, replacement);
        }

        foreach (var retired in legacy)
            if (retired.Id != renamedId) repository.DeleteContact(retired.Id, deleteHistory: false);
        return current;
    }

    private static bool IsLegacyDefault(Contact contact) =>
        contact.BindingStatus == "demo" && contact.NameSource == "chat_title" &&
        contact.AddressTermSource == "unknown" && contact.AddressEvidence is null && contact.IdentityLabel is null &&
        contact.RemarkName == contact.DisplayName && contact.AddressTerm == contact.DisplayName &&
        (contact.DisplayName, contact.Relationship) is ("陈老师", "teacher") or ("小林", "friend");
}
