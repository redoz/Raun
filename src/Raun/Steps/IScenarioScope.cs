namespace Raun;

internal interface IScenarioScope
{
    T Steps<T>() where T : Phase;
}
