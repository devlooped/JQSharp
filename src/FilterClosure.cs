using System.Text.Json;

namespace Devlooped;

public sealed record FilterClosure(JqFilter Filter, JqEnvironment CapturedEnv);
