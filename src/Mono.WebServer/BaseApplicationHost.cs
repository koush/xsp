// Mono.WebServer.BaseApplicationHost
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//	Lluis Sanchez Gual (lluis@ximian.com)
//
// (C) Copyright 2004 Novell, Inc
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Configuration;
using System.Collections.Generic;

namespace Mono.WebServer
{
	public class BaseApplicationHost : MarshalByRefObject, IApplicationHost
	{
		static readonly ReaderWriterLockSlim handlersCacheLock = new ReaderWriterLockSlim ();
		
		string path;
		string vpath;
		IRequestBroker requestBroker;
		EndOfRequestHandler endOfRequest;
		ApplicationServer appserver;
		Dictionary <string, bool> handlersCache;		

		public BaseApplicationHost ()
		{
			endOfRequest = new EndOfRequestHandler (EndOfRequest);
			AppDomain.CurrentDomain.DomainUnload += new EventHandler (OnUnload);
		}

		public void Unload ()
		{
			HttpRuntime.UnloadAppDomain ();
		}

		public void OnUnload (object o, EventArgs args)
		{
			if (appserver != null)
				appserver.DestroyHost (this);
		}

		public override object InitializeLifetimeService ()
		{
			return null; // who wants to live forever?
		}

		public ApplicationServer Server {
			get { return appserver; }
			set { appserver = value; }
		}

		public string Path {
			get {
				if (path == null)
					path = AppDomain.CurrentDomain.GetData (".appPath").ToString ();

				return path;
			}
		}

		public string VPath {
			get {
				if (vpath == null)
					vpath =  AppDomain.CurrentDomain.GetData (".appVPath").ToString ();

				return vpath;
			}
		}

		public AppDomain Domain {
			get { return AppDomain.CurrentDomain; }
		}

		public IRequestBroker RequestBroker
		{
			get { return requestBroker; }
			set { requestBroker = value; }
		}

		protected void ProcessRequest (MonoWorkerRequest mwr)
		{
			if (mwr == null) {
				EndOfRequest (mwr);
				return;
			}
			
			if (!mwr.ReadRequestData ()) {
				EndOfRequest (mwr);
				return;
			}
			
			mwr.EndOfRequestEvent += endOfRequest;
			try {
				mwr.ProcessRequest ();
			} catch (ThreadAbortException) {
				Thread.ResetAbort ();
			} catch (Exception ex) { // should "never" happen
				// we don't know what the request state is,
				// better write the exception to the console
				// than forget it.
				Console.WriteLine ("Unhandled exception: {0}", ex);
				EndOfRequest (mwr);
			}
		}

		public void EndOfRequest (MonoWorkerRequest mwr)
		{
			try {
				mwr.CloseConnection ();
			} catch {
			} finally {
				BaseRequestBroker brb = requestBroker as BaseRequestBroker;
				if (brb != null)
					brb.UnregisterRequest (mwr.RequestId);
			}
		}

		public virtual bool IsHttpHandler (string verb, string uri)
		{
			string cacheKey = verb + "_" + uri;
			bool locked;

			locked = false;
			try {
				handlersCacheLock.EnterReadLock ();

				locked = true;
				if (handlersCache != null) {
					bool found;
					if (handlersCache.TryGetValue (cacheKey, out found))
						return found;
				} else {
					handlersCache = new Dictionary <string, bool> ();
				}
			} finally {
				if (locked)
					handlersCacheLock.ExitReadLock ();
			}
			
			
			bool handlerFound = LocateHandler (verb, uri);
			locked = false;
			try {
				handlersCacheLock.EnterWriteLock ();

				locked = true;
				if (handlersCache.ContainsKey (cacheKey))
					handlersCache [cacheKey] = handlerFound;
				else
					handlersCache.Add (cacheKey, handlerFound);
			} finally {
				if (locked)
					handlersCacheLock.ExitWriteLock ();
			}
			
			return handlerFound;
		}

		bool LocateHandler (string verb, string uri)
		{
			HttpHandlersSection config = WebConfigurationManager.GetSection ("system.web/httpHandlers") as HttpHandlersSection;
			HttpHandlerActionCollection handlers = config != null ? config.Handlers : null;
			int count = handlers != null ? handlers.Count : 0;
			
			if (count == 0)
				return false;

			HttpHandlerAction handler;
			string[] verbs;
			for (int i = 0; i < count; i++) {
				handler = handlers [i];
				verbs = SplitVerbs (handler.Verb);

				if (verbs == null) {
					if (PathMatches (handler, uri))
						return true;
					continue;
				}

				for (int j = 0; j < verbs.Length; j++) {
					if (verbs [j] != verb)
						continue;
					if (PathMatches (handler, uri))
						return true;
				}
			}

			return false;
		}

		bool PathMatches (HttpHandlerAction handler, string uri)
		{
			bool result = false;
			string[] handlerPaths = handler.Path.Split (',');
			int slash = uri.LastIndexOf ('/');
			string origUri = uri;
			if (slash != -1)
				uri = uri.Substring (slash);

			SearchPattern sp = null;
			foreach (string handlerPath in handlerPaths) {
				if (handlerPath == "*")
					continue; // ignore
				
				string matchExact = null;
				string endsWith = null;
				Regex regEx = null;

				if (handlerPath.Length > 0) {
					if (handlerPath [0] == '*' && (handlerPath.IndexOf ('*', 1) == -1))
						endsWith = handlerPath.Substring (1);

					if (handlerPath.IndexOf ('*') == -1)
						if (handlerPath [0] != '/') {
							HttpContext ctx = HttpContext.Current;
							HttpRequest req = ctx != null ? ctx.Request : null;
							string vpath = HttpRuntime.AppDomainAppVirtualPath;

							if (vpath == "/")
								vpath = String.Empty;

							matchExact = String.Concat (vpath, "/", handlerPath);
						}
				}

				if (matchExact != null) {
					result = matchExact.Length == origUri.Length && origUri.EndsWith (matchExact, StringComparison.OrdinalIgnoreCase);
					if (result == true)
						break;
					else
						continue;
				} else if (endsWith != null) {
					result = uri.EndsWith (endsWith, StringComparison.OrdinalIgnoreCase);
					if (result == true)
						break;
					else
						continue;
				}

				string pattern;
				if (handlerPath.Length > 0 && handlerPath [0] == '/')
					pattern = handlerPath.Substring (1);
				else
					pattern = handlerPath;

				if (sp == null)
					sp = new SearchPattern (pattern, true);
				else
					sp.SetPattern (pattern, true);
				
				if (sp.IsMatch (origUri)) {
					result = true;
					break;
				}				
			}
			
			return result;
		}
		
		string[] SplitVerbs (string verb)
		{
			if (verb == "*")
				return null;

			return verb.Split (',');
		}
	}
}

