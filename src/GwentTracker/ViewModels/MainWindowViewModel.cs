﻿using GwentTracker.Model;
using ReactiveUI;
using ReactiveUI.Legacy;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows;
using W3SavegameEditor.Core.Savegame;
using W3SavegameEditor.Core.Savegame.Variables;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace GwentTracker.ViewModels
{
    public class MainWindowViewModel : ReactiveObject, ISupportsActivation
    {
        private readonly string _textureStringFormat;
        private readonly ReactiveList<CardViewModel> _cards;
        private bool _initialLoadComplete = false;

        public IReactiveDerivedList<CardViewModel> Cards { get; set; }
        public ReactiveList<Message> Messages { get; set; }
        public Subject<string> Notifications { get; set; }
        public ReactiveCommand<string, SaveGameInfo> Load { get; set; }
        public ReactiveCommand<Unit, IEnumerable<Card>> LoadCards { get; set; }
        public ReactiveCommand AddFilter { get; set; }
        public ReactiveCommand RemoveFilter { get; set; }
        public ReactiveList<string> Filters { get; set; }

        ObservableAsPropertyHelper<Visibility> _loaderVisibility;
        public Visibility LoaderVisibility => _loaderVisibility.Value;

        private ObservableAsPropertyHelper<Visibility> _cardVisiblity;
        public Visibility CardVisibility => _cardVisiblity.Value;

        private CardViewModel _selectedCard;
        public CardViewModel SelectedCard
        {
            get { return _selectedCard; }
            set { this.RaiseAndSetIfChanged(ref _selectedCard, value); }
        }

        private SaveGameInfo _model;

        private SaveGameInfo Model
        {
            get { return _model; }
            set { this.RaiseAndSetIfChanged(ref _model, value); }
        }

        private string _filterString;
        public string FilterString
        {
            get { return _filterString; }
            set { this.RaiseAndSetIfChanged(ref _filterString, value); }
        }

        private string _saveGamePath;
        public string SaveGamePath
        {
            get { return _saveGamePath; }
            set { this.RaiseAndSetIfChanged(ref _saveGamePath, value); }
        }

        public MainWindowViewModel(string saveGamePath, string textureStringFormat)
        {
            _textureStringFormat = textureStringFormat;
            Activator = new ViewModelActivator();
            Filters = new ReactiveList<string>();
            _cards = new ReactiveList<CardViewModel>();
            _cards.ChangeTrackingEnabled = true;
            Cards = _cards.CreateDerivedCollection(c => c, c => !c.IsHidden);
            Messages = new ReactiveList<Message>();
            Notifications = new Subject<string>();

            this.WhenActivated(d =>
            {
                LoadCards = ReactiveCommand.CreateFromTask(LoadCardsFromFiles);
                LoadCards.ThrownExceptions.Subscribe(e =>
                {
                    Log.Error(e, "Unable to load card data");
                    Notifications.OnNext("Unable to load card info");
                });
                LoadCards.Subscribe(cards =>
                {
                    var mapped = cards.Select(c => new CardViewModel(_textureStringFormat)
                    {
                        Index = c.Index,
                        Copies = c.Copies,
                        Name = c.Name,
                        Flavor = c.Flavor,
                        Obtained = c.Obtained,
                        Deck = c.Deck,
                        Type = c.Type,
                        Locations = c.Locations
                    });

                    _cards.Clear();
                    foreach (var card in mapped)
                        _cards.Add(card);
                    //Cards.AddRange(cards);
                    SaveGamePath = saveGamePath;
                });

                Load = ReactiveCommand.CreateFromTask<string, SaveGameInfo>(LoadSaveGame);
                Load.Subscribe(OnSaveGameLoaded);
                Load.ThrownExceptions.Subscribe(e =>
                {
                    Log.Error(e, "Unable to load save game at {path}", SaveGamePath);
                    Notifications.OnNext("Unable to load save game");
                });
                _loaderVisibility = Load.IsExecuting
                    .Select(x => x ? Visibility.Visible : Visibility.Collapsed)
                    .ToProperty(this, x => x.LoaderVisibility, Visibility.Collapsed);

                this.WhenAnyValue(x => x.SaveGamePath)
                    .Select(s => s?.Trim())
                    .DistinctUntilChanged()
                    .Where(s => !string.IsNullOrEmpty(s))
                    .InvokeCommand(Load)
                    .DisposeWith(d);

                _cardVisiblity = this.WhenAnyValue(x => x.SelectedCard)
                    .Select(c => c == null ? Visibility.Collapsed : Visibility.Visible)
                    .ToProperty(this, x => x.CardVisibility, Visibility.Collapsed);

                this.Filters.Changed
                    .Subscribe(i =>
                    {
                        var culture = CultureInfo.CurrentUICulture;

                        foreach (var card in _cards)
                            card.IsHidden = ShouldFilterCard(card);
                    })
                    .DisposeWith(d);

                var canAddFilter = this.WhenAnyValue(
                    vm => vm.Model.CardCopies,
                    vm => vm.FilterString,
                    (cards, filter) => cards?.Any() == true && !string.IsNullOrWhiteSpace(filter));
                AddFilter = ReactiveCommand.Create(OnAddFilter, canAddFilter);
                RemoveFilter = ReactiveCommand.Create<string>(OnRemoveFilter);
            });
        }

        private async Task<IEnumerable<Card>> LoadCardsFromFiles()
        {
            var cards = new List<Card>();
            var files = new[] { "monsters", "neutral", "nilfgaard", "northernrealms", "scoiatael" };
            var deserializer = new DeserializerBuilder()
                                    .IgnoreUnmatchedProperties()
                                    .WithNamingConvention(new CamelCaseNamingConvention())
                                    .Build();

            foreach (var file in files)
            {
                var filePath = Path.Combine("data", $"{file}.yml");
                try
                {
                    using (var reader = File.OpenText(filePath))
                    {
                        var contents = await reader.ReadToEndAsync();
                        cards.AddRange(deserializer.Deserialize<List<Card>>(contents));
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Couldn't load card data from {file}", filePath);
                    throw;
                }
            }

            return cards;
        }

        private async Task<SaveGameInfo> LoadSaveGame(string path)
        {
            var saveGame = await SavegameFile.ReadAsync(path);
            var cardCollection = ((BsVariable)saveGame.Variables[11]).Variables
                                                                     .Skip(2)
                                                                     .TakeWhile(v => v.Name != "SBSelectedDeckIndex")
                                                                     .Where(v => v.Name == "cardIndex" || v.Name == "numCopies")
                                                                     .ToArray();
            var cards = new List<KeyValuePair<int, int>>(cardCollection.Length);
            for (var i = 0; i < cardCollection.Length; i += 2)
            {
                var index = ((VariableValue<int>)((VlVariable)cardCollection[i]).Value).Value;
                var copies = ((VariableValue<int>)((VlVariable)cardCollection[i + 1]).Value).Value;
                cards.Add(new KeyValuePair<int, int>(index, copies));
            }

            // TODO: figure out how quest info is stored and pull out the quests that cause missable cards

            return new SaveGameInfo
            {
                CardCopies = cards
            };
        }

        private void OnSaveGameLoaded(SaveGameInfo info)
        {
            Model = info;
            foreach (var copy in info.CardCopies)
            {
                var card = _cards.Where(c => c.Index == copy.Key).SingleOrDefault();

                if (card != null)
                {
                    if (_initialLoadComplete && (card.Obtained == false || card.Copies < copy.Value))
                        Notifications.OnNext($"Obtained {card.Name}");

                    card.Obtained = true;
                    card.Copies = copy.Value;
                }
            }

            // TODO: remove this missable quest placeholder
            if (!_initialLoadComplete)
            {
                Messages.Add(new Message
                {
                    Description = "The description",
                    Name = "Missable Card"
                });
            }

            _initialLoadComplete = true;
        }

        private bool ShouldFilterCard(CardViewModel card)
        {
            var compareInfo = CultureInfo.CurrentUICulture.CompareInfo;

            return Filters.Any() &&
                   !Filters.All(f => compareInfo.IndexOf(card.Name, f, CompareOptions.IgnoreCase) >= 0 ||
                                     compareInfo.IndexOf(card.Deck, f, CompareOptions.IgnoreCase) >= 0 ||
                                     compareInfo.IndexOf(card.Type ?? "", f, CompareOptions.IgnoreCase) >= 0 ||
                                     compareInfo.IndexOf(card.Location, f, CompareOptions.IgnoreCase) >= 0 ||
                                     compareInfo.IndexOf(card.Region, f, CompareOptions.IgnoreCase) >= 0);
        }

        private void OnAddFilter()
        {
            Filters.Add(FilterString);
            FilterString = null;
        }

        private void OnRemoveFilter(string filter)
        {
            Filters.Remove(filter);
        }

        public ViewModelActivator Activator { get; }
    }
}
