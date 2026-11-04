namespace RimWise.Integration.AI;

public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}
