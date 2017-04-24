﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JsonRpc;
using Lsp.Capabilities.Client;
using Lsp.Capabilities.Server;
using Lsp.Handlers;
using Lsp.Models;
using Lsp.Protocol;
using Newtonsoft.Json.Linq;

namespace Lsp
{
    public class LanguageServer : IInitializeHandler, ILanguageServer, IDisposable
    {
        private readonly ITextDocumentSyncHandler _textDocumentSyncHandler;
        private readonly Connection _connection;
        private readonly LspRequestRouter _requestRouter;
        private readonly ShutdownHandler _shutdownHandler = new ShutdownHandler();
        private readonly ExitHandler _exitHandler;
        private ClientVersion? _clientVersion;
        private readonly HandlerCollection _collection = new HandlerCollection();
        private readonly IResponseRouter _responseRouter;
        private readonly LspReciever _reciever;
        private readonly TaskCompletionSource<InitializeResult> _initializeComplete = new TaskCompletionSource<InitializeResult>();
        private readonly CompositeDisposable _disposable = new CompositeDisposable();

        public LanguageServer(TextReader input, TextWriter output, ITextDocumentSyncHandler textDocumentSyncHandler)
        {
            _textDocumentSyncHandler = textDocumentSyncHandler;
            var outputHandler = new OutputHandler(output);
            _requestRouter = new LspRequestRouter(_collection, textDocumentSyncHandler);
            _responseRouter = new ResponseRouter(outputHandler);
            _reciever = new LspReciever();

            _connection = new Connection(
                input,
                outputHandler,
                _reciever,
                new RequestProcessIdentifier(),
                _requestRouter,
                _responseRouter);

            _exitHandler = new ExitHandler(_shutdownHandler);

            _disposable.Add(
                AddHandler(this),
                AddHandler(_shutdownHandler),
                AddHandler(_exitHandler),
                AddHandler(_textDocumentSyncHandler),
                AddHandler(new CancelRequestHandler(_requestRouter))
            );
        }

        internal LanguageServer(
            TextReader input,
            IOutputHandler output,
            LspReciever reciever,
            IRequestProcessIdentifier requestProcessIdentifier,
            LspRequestRouter requestRouter,
            IResponseRouter responseRouter
        )
        {
            _reciever = reciever;
            _requestRouter = requestRouter;
            _connection = new Connection(input, output, reciever, requestProcessIdentifier, requestRouter, responseRouter);

            _exitHandler = new ExitHandler(_shutdownHandler);

            _disposable.Add(
                AddHandler(this),
                AddHandler(_shutdownHandler),
                AddHandler(_exitHandler),
                AddHandler(new CancelRequestHandler(_requestRouter))
            );
        }

        public InitializeParams Client { get; private set; }
        public InitializeResult Server { get; private set; }

        public IDisposable AddHandler(IJsonRpcHandler handler)
        {
            var handlerDisposable = _collection.Add(handler);

            return new ImutableDisposable(
                handlerDisposable,
                new Disposable(() => {
                    var handlers = _collection
                        .Where(x => x.Handler == handler)
                        .Where(x => x.AllowsDynamicRegistration)
                        .Select(x => x.Registration)
                        .Where(x => x != null)
                        .ToArray();

                    Task.Run(() => this.UnregisterCapability(new UnregistrationParams() { Unregisterations = handlers }));
                }));
        }

        public async Task Initialize()
        {
            _connection.Open();

            Server = await _initializeComplete.Task;

            _reciever.Initialized();

            // Small delay to let client respond
            await Task.Delay(20);

            await DynamicallyRegisterHandlers();
        }

        async Task<InitializeResult> IRequestHandler<InitializeParams, InitializeResult>.Handle(InitializeParams request, CancellationToken token)
        {
            Client = request;

            _clientVersion = request.Capabilities.GetClientVersion();

            if (_clientVersion == ClientVersion.Lsp3)
            {
                // handle client capabilites
                if (request.Capabilities.TextDocument != null)
                {
                    ProcessCapabilties(request.Capabilities.TextDocument);
                }

                if (request.Capabilities.Workspace != null)
                {
                    ProcessCapabilties(request.Capabilities.Workspace);
                }
            }

            var serverCapabilities = new ServerCapabilities() {
                CodeActionProvider = HasHandler<ICodeActionHandler>(),
                CodeLensProvider = GetOptions<ICodeLensOptions, CodeLensOptions>(CodeLensOptions.Of),
                CompletionProvider = GetOptions<ICompletionOptions, CompletionOptions>(CompletionOptions.Of),
                DefinitionProvider = HasHandler<IDefinitionHandler>(),
                DocumentFormattingProvider = HasHandler<IDocumentFormattingHandler>(),
                DocumentHighlightProvider = HasHandler<IDocumentHighlightHandler>(),
                DocumentLinkProvider = GetOptions<IDocumentLinkOptions, DocumentLinkOptions>(DocumentLinkOptions.Of),
                DocumentOnTypeFormattingProvider = GetOptions<IDocumentOnTypeFormattingOptions, DocumentOnTypeFormattingOptions>(DocumentOnTypeFormattingOptions.Of),
                DocumentRangeFormattingProvider = HasHandler<IDocumentRangeFormattingHandler>(),
                DocumentSymbolProvider = HasHandler<IDocumentSymbolHandler>(),
                ExecuteCommandProvider = GetOptions<IExecuteCommandOptions, ExecuteCommandOptions>(ExecuteCommandOptions.Of),
                HoverProvider = HasHandler<IHoverHandler>(),
                ReferencesProvider = HasHandler<IReferencesHandler>(),
                RenameProvider = HasHandler<IRenameHandler>(),
                SignatureHelpProvider = GetOptions<ISignatureHelpOptions, SignatureHelpOptions>(SignatureHelpOptions.Of),
                WorkspaceSymbolProvider = HasHandler<IWorkspaceSymbolsHandler>()
            };

            var textSyncHandler = _collection
                .Select(x => x.Handler)
                .OfType<ITextDocumentSyncHandler>()
                .FirstOrDefault();

            if (_clientVersion == ClientVersion.Lsp2)
            {
                serverCapabilities.TextDocumentSync = textSyncHandler?.Options.Change ?? TextDocumentSyncKind.None;
            }
            else
            {
                serverCapabilities.TextDocumentSync = textSyncHandler?.Options ?? new TextDocumentSyncOptions() {
                    Change = TextDocumentSyncKind.None,
                    OpenClose = false,
                    Save = new SaveOptions() { IncludeText = false },
                    WillSave = false,
                    WillSaveWaitUntil = false
                };
            }

            // TODO: Need a call back here
            // serverCapabilities.Experimental;

            var result = new InitializeResult() { Capabilities = serverCapabilities };
            _initializeComplete.SetResult(result);
            return result;
        }

        private bool HasHandler<T>()
        {
            return _collection.Any(z => z.HandlerType == typeof(T));
        }

        private T GetOptions<O, T>(Func<O, T> action)
            where T : class
        {
            return _collection
                .Select(x => x.Registration?.RegisterOptions is O cl ? action(cl) : null)
                .FirstOrDefault(x => x != null);
        }

        private void ProcessCapabilties(object instance)
        {
            var values = instance
                .GetType()
                .GetTypeInfo()
                .DeclaredProperties
                .Where(x => x.CanRead)
                .Select(x => x.GetValue(instance))
                .OfType<ISupports>();

            foreach (var value in values)
            {
                foreach (var handler in _collection.Where(x => x.HasCapability && x.CapabilityType == value.ValueType))
                {
                    handler.SetCapability(value.Value);
                }
            }
        }

        private async Task DynamicallyRegisterHandlers()
        {
            var registrations = new List<Registration>();
            foreach (var handler in _collection.Where(x => x.AllowsDynamicRegistration))
            {
                registrations.Add(handler.Registration);
            }

            var @params = new RegistrationParams() { Registrations = registrations };

            await this.RegisterCapability(@params);
        }

        public event ShutdownEventHandler Shutdown
        {
            add => _shutdownHandler.Shutdown += value;
            remove => _shutdownHandler.Shutdown -= value;
        }

        public event ExitEventHandler Exit
        {
            add => _exitHandler.Exit += value;
            remove => _exitHandler.Exit -= value;
        }

        public Task SendNotification<T>(string method, T @params)
        {
            return _responseRouter.SendNotification(method, @params);
        }

        public Task<TResponse> SendRequest<T, TResponse>(string method, T @params)
        {
            return _responseRouter.SendRequest<T, TResponse>(method, @params);
        }

        public Task SendRequest<T>(string method, T @params)
        {
            return _responseRouter.SendRequest(method, @params);
        }

        public TaskCompletionSource<JToken> GetRequest(long id)
        {
            return _responseRouter.GetRequest(id);
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _disposable?.Dispose();
        }
    }
}