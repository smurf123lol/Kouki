using Kouki.Mugura.Commands;
using Kouki.Mugura.Registry;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Factories;

public interface ICommandFactory
{
    ICommand? Create(CommandEntry entry, JsonNode? argument);
}