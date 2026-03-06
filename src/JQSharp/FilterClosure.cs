using System.Text.Json;

namespace Devlooped;

sealed record FilterClosure(JqFilter Filter, JqEnvironment CapturedEnv);
