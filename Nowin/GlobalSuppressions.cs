
// This file is used by Code Analysis to maintain SuppressMessage 
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given 
// a specific target and scoped to a namespace, type, member, etc.

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Common Practices and Code Improvements", "RECS0033:Convert 'if' to '||' expression", Justification = "Cannot be similified - bool could be set to true from inside and still returning false", Scope = "member", Target = "~M:Nowin.Transport2HttpHandler.ProcessReceive~System.Boolean")]

