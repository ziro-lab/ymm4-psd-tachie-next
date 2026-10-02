using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PsdTachieNext.Core;
using YukkuriMovieMaker.Project;

namespace PsdTachieNext.Ymm4;

/// <summary>Public CurrentCharacter observable only. No enumeration/prefetch of the character list.</summary>
internal sealed class SelectedCharacterPrefetchBinding : IDisposable
{
    private IDisposable? subscription;
    private Character? character;
    private CompiledItemParameter? parameter;
    private SourceAssetRef? lastSource;
    private long epoch;
    private bool disposed;
    internal Exception? Error { get; private set; }
    internal void Bind(object? currentCharacter)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var captured=++epoch;subscription?.Dispose();subscription=null;
        DetachCharacter();lastSource=null;CompiledTachieSource.PrefetchSelected(null);
        if(disposed||currentCharacter is not IObservable<Character> observable)return;
        subscription=observable.Subscribe(new Observer(value=>
        {
            void Apply(){if(!disposed&&captured==epoch)SelectCharacter(value);}
            var dispatcher=Application.Current.Dispatcher;
            if(dispatcher.CheckAccess())Apply();else dispatcher.BeginInvoke(new Action(Apply),DispatcherPriority.Background);
        },error=>Error=error));
    }
    private void SelectCharacter(Character? next)
    {
        if(ReferenceEquals(character,next))return;
        DetachCharacter();character=next;
        if(character is INotifyPropertyChanged changed)changed.PropertyChanged+=CharacterChanged;
        BindParameter(force:true);
    }
    private void CharacterChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(string.IsNullOrEmpty(e.PropertyName)||e.PropertyName is nameof(Character.TachieDefaultItemParameter) or nameof(Character.TachieType))BindParameter();
    }
    private void BindParameter(bool force=false)
    {
        if(parameter is INotifyPropertyChanged old)old.PropertyChanged-=ParameterChanged;
        parameter=character?.TachieType==typeof(CompiledTachiePlugin)?character.TachieDefaultItemParameter as CompiledItemParameter:null;
        if(parameter is INotifyPropertyChanged current)current.PropertyChanged+=ParameterChanged;
        Refresh(force);
    }
    private void ParameterChanged(object? sender,PropertyChangedEventArgs e)
    {if(string.IsNullOrEmpty(e.PropertyName)||e.PropertyName is nameof(CompiledItemParameter.Source) or nameof(CompiledItemParameter.File))Refresh();}
    private void Refresh(bool force=false)
    {
        var source=parameter?.Source;if(!force&&lastSource==source)return;lastSource=source;
        try{CompiledTachieSource.PrefetchSelected(source);Error=null;}
        catch(Exception error){Error=error;CompiledTachieSource.PrefetchSelected(null);}
    }
    private void DetachCharacter()
    {
        if(character is INotifyPropertyChanged old)old.PropertyChanged-=CharacterChanged;
        if(parameter is INotifyPropertyChanged previous)previous.PropertyChanged-=ParameterChanged;
        character=null;parameter=null;
    }
    public void Dispose(){disposed=true;epoch++;subscription?.Dispose();subscription=null;DetachCharacter();CompiledTachieSource.PrefetchSelected(null);}
    private sealed class Observer(Action<Character?> next,Action<Exception> error):IObserver<Character>
    {public void OnNext(Character value)=>next(value);public void OnError(Exception value)=>error(value);public void OnCompleted(){} }
}
