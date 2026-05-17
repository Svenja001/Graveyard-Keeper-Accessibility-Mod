namespace GameBalanceDumper;

// Skip Unity object refs and [NonSerialized] fields - serializing them pulls in scene state or crashes.
internal sealed class UnityObjectIgnoringResolver : DefaultContractResolver
{
    public override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var prop = base.CreateProperty(member, memberSerialization);

        var declared = (member as FieldInfo)?.FieldType
                    ?? (member as PropertyInfo)?.PropertyType;

        if (declared != null && typeof(UnityEngine.Object).IsAssignableFrom(declared))
        {
            prop.Ignored = true;
            prop.ShouldSerialize = _ => false;
        }

        if (member is FieldInfo fi && fi.IsNotSerialized)
        {
            prop.Ignored = true;
            prop.ShouldSerialize = _ => false;
        }

        return prop;
    }
}
