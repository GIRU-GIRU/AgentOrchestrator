#include "OrchestratorStatus.h"

#include "Async/Async.h"
#include "Dom/JsonObject.h"
#include "Editor.h"
#include "Framework/Application/SlateApplication.h"
#include "Framework/Notifications/NotificationManager.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Widgets/Notifications/SNotificationList.h"

#define LOCTEXT_NAMESPACE "AgentOrchestrator"

namespace OrchestratorStatusPrivate
{
	static constexpr const TCHAR* CONFIG_SECTION = TEXT("AgentOrchestrator");
	static constexpr const TCHAR* CONFIG_STOP_PIE = TEXT("bStopAgentPieOnTake");
	static constexpr const TCHAR* COMMAND_SHELL = TEXT("cmd.exe");
	static constexpr const TCHAR* MASTER_AGENT_NAME = TEXT("MASTER AGENT");
	static constexpr const TCHAR* MANAGER_LIVE_FILE = TEXT("AgentManager/live.json");
	static constexpr const TCHAR* MANAGER_CLOSED_STATE = TEXT("Closed");
	static constexpr float ERROR_TOAST_SECONDS = 8.f;

	FString Str(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		FString Value;
		Object->TryGetStringField(Field, Value);
		return Value;
	}

	double Num(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		double Value = 0.0;
		Object->TryGetNumberField(Field, Value);
		return Value;
	}

	bool Bool(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		bool bValue = false;
		Object->TryGetBoolField(Field, bValue);
		return bValue;
	}

	TArray<FString> Strings(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		TArray<FString> Values;
		Object->TryGetStringArrayField(Field, Values);
		return Values;
	}

	FOrchestratorGrant ParseGrant(const TSharedPtr<FJsonObject>& Object)
	{
		FOrchestratorGrant Grant;
		Grant.Id = Str(Object, TEXT("id"));
		Grant.Owner = Str(Object, TEXT("owner"));
		Grant.DisplayName = Str(Object, TEXT("displayName"));
		Grant.SessionId = Str(Object, TEXT("sessionId"));
		Grant.Liveness = Str(Object, TEXT("liveness"));
		Grant.Kind = Str(Object, TEXT("kind"));
		Grant.Activity = Str(Object, TEXT("activity"));
		Grant.Purpose = Str(Object, TEXT("purpose"));
		Grant.LastCommand = Str(Object, TEXT("lastCommand"));
		Grant.bImplicit = Bool(Object, TEXT("implicit"));
		Grant.bOwnsPie = Bool(Object, TEXT("ownsPie"));
		Grant.QueuedAt = Num(Object, TEXT("queuedAt"));
		Grant.AcquiredAt = Num(Object, TEXT("acquiredAt"));
		Grant.TurnDueAt = Num(Object, TEXT("turnDueAt"));
		Grant.TurnBackAt = Num(Object, TEXT("turnBackAt"));
		Grant.Position = static_cast<int32>(Num(Object, TEXT("position")));
		Grant.EditorPid = static_cast<uint32>(Num(Object, TEXT("editorPid")));
		return Grant;
	}

	void ParseGrants(const TSharedPtr<FJsonObject>& Root, const TCHAR* Field, TArray<FOrchestratorGrant>& Out)
	{
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Root->TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				const TSharedPtr<FJsonObject>* Object = nullptr;
				if (Value->TryGetObject(Object))
				{
					Out.Add(ParseGrant(*Object));
				}
			}
		}
	}
}

bool FOrchestratorEditor::IsThisWindow() const
{
	return Pid == FPlatformProcess::GetCurrentProcessId();
}

const FOrchestratorAgent* FOrchestratorSnapshot::FindAgent(const FString& Owner) const
{
	return Agents.FindByPredicate([&Owner](const FOrchestratorAgent& Agent) { return Agent.Owner == Owner; });
}

const FOrchestratorGrant* FOrchestratorSnapshot::FindQueued(const FString& Id) const
{
	return Queue.FindByPredicate([&Id](const FOrchestratorGrant& Grant) { return Grant.Id == Id; });
}

const FOrchestratorTask* FOrchestratorSnapshot::FindTask(const FString& Id) const
{
	return Tasks.FindByPredicate([&Id](const FOrchestratorTask& Task) { return Task.Id == Id; });
}

const FOrchestratorEditor* FOrchestratorSnapshot::FindEditor(uint32 Pid) const
{
	return Editors.FindByPredicate([Pid](const FOrchestratorEditor& Editor) { return Editor.Pid == Pid; });
}

bool FManagerAgent::IsMasterAgent() const
{
	// The manager titles him with this name alone, and refuses it as a rename for anyone else.
	return Name == OrchestratorStatusPrivate::MASTER_AGENT_NAME;
}

bool FManagerAgent::IsQuiet() const
{
	return State == TEXT("Idle") || State == TEXT("Finished") || State == TEXT("Paused");
}

FString FManagerAgent::SessionId() const
{
	FString Id;
	return Key.Split(TEXT(":"), nullptr, &Id) ? Id : Key;
}

FOrchestratorStatus::FOrchestratorStatus()
{
	GConfig->GetBool(OrchestratorStatusPrivate::CONFIG_SECTION, OrchestratorStatusPrivate::CONFIG_STOP_PIE, bStopAgentPieOnTake, GEditorPerProjectIni);
}

FOrchestratorStatus::~FOrchestratorStatus()
{
	Stop();
}

void FOrchestratorStatus::Start()
{
	if (!TickerHandle.IsValid())
	{
		TickerHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateSP(this, &FOrchestratorStatus::Tick), POLL_INTERVAL_SECONDS);
		StartPieHandle = FEditorDelegates::StartPIE.AddSP(this, &FOrchestratorStatus::OnStartPie);
		ReadStatusFile(true);
		ReportPieState();
	}
}

void FOrchestratorStatus::Stop()
{
	if (TickerHandle.IsValid())
	{
		FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle);
		TickerHandle.Reset();
		FEditorDelegates::StartPIE.Remove(StartPieHandle);
		IFileManager::Get().Delete(*GetPieReportPath(), false, false, true);
	}
}

double FOrchestratorStatus::NowUnix()
{
	return FDateTime::UtcNow().ToUnixTimestampDecimal();
}

FText FOrchestratorStatus::FormatAge(double UnixTime)
{
	if (UnixTime <= 0.0)
	{
		return LOCTEXT("AgeNever", "-");
	}
	return FormatDuration(NowUnix() - UnixTime);
}

FText FOrchestratorStatus::FormatDuration(double InSeconds)
{
	int64 Seconds = FMath::Max<int64>(0, static_cast<int64>(InSeconds));
	if (Seconds < 90)
	{
		return FText::Format(LOCTEXT("AgeSeconds", "{0}s"), FText::AsNumber(Seconds));
	}
	if (Seconds < 90 * 60)
	{
		return FText::Format(LOCTEXT("AgeMinutes", "{0}m"), FText::AsNumber(Seconds / 60));
	}
	FNumberFormattingOptions OneDecimal;
	OneDecimal.MinimumFractionalDigits = 1;
	OneDecimal.MaximumFractionalDigits = 1;
	return FText::Format(LOCTEXT("AgeHours", "{0}h"), FText::AsNumber(Seconds / 3600.0, &OneDecimal));
}

bool FOrchestratorStatus::IsStale() const
{
	return !Snapshot.bValid || SecondsSinceUpdate() > STALE_AFTER_SECONDS;
}

double FOrchestratorStatus::SecondsSinceUpdate() const
{
	return Snapshot.bValid ? NowUnix() - Snapshot.GeneratedAt : TNumericLimits<double>::Max();
}

void FOrchestratorStatus::SetStopAgentPieOnTake(bool bValue)
{
	bStopAgentPieOnTake = bValue;
	GConfig->SetBool(OrchestratorStatusPrivate::CONFIG_SECTION, OrchestratorStatusPrivate::CONFIG_STOP_PIE, bValue, GEditorPerProjectIni);
}

FString FOrchestratorStatus::GetStateDir() const
{
	// orch.py honours the same override, so tests and odd setups stay in step.
	FString Override = FPlatformMisc::GetEnvironmentVariable(TEXT("ORCH_STATE"));
	return Override.IsEmpty() ? FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("Orchestration")) : Override;
}

FString FOrchestratorStatus::GetStatusFilePath() const
{
	return GetStateDir() / TEXT("orch-status.json");
}

FString FOrchestratorStatus::GetJournalPath() const
{
	return GetStateDir() / TEXT("orch-events.log");
}

FString FOrchestratorStatus::GetEditorsDir() const
{
	return GetStateDir() / TEXT("editors");
}

FString FOrchestratorStatus::GetPieReportPath() const
{
	return GetEditorsDir() / FString::Printf(TEXT("%u.json"), FPlatformProcess::GetCurrentProcessId());
}

const TCHAR* FOrchestratorStatus::GetOrchShell()
{
	return OrchestratorStatusPrivate::COMMAND_SHELL;
}

FString FOrchestratorStatus::GetOrchScriptPath() const
{
	FString Script = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() / TEXT(".claude/scripts/orch.cmd"));
	FPaths::MakePlatformFilename(Script);
	return Script;
}

FString FOrchestratorStatus::GetOrchParams(const FString& Args) const
{
	// /s drops the outer pair of quotes, so a command line that starts with a quoted path survives. ExecProcess creates it with no window.
	return FString::Printf(TEXT("/d /s /c \"\"%s\" %s\""), *GetOrchScriptPath(), *Args);
}

void FOrchestratorStatus::NotePieStart()
{
	if (PieSince <= 0.0)
	{
		PieSince = NowUnix();
		// Clicking Play is input, and a script starting PIE is none, so this is what tells the user's PIE from an agent's. Input that is merely nearby reads as the user's, the safe way round.
		bPieByInput = FSlateApplication::IsInitialized() && FPlatformTime::Seconds() - FSlateApplication::Get().GetLastUserInteractionTime() < PIE_START_INPUT_WINDOW_SECONDS;
	}
}

void FOrchestratorStatus::OnStartPie(bool bIsSimulating)
{
	// The map load that follows blocks the ticker for as long as it takes, so the orchestrator has to hear about this PIE now.
	NotePieStart();
	WritePieReport(true);
}

void FOrchestratorStatus::ReportPieState()
{
	bool bPie = GEditor && GEditor->IsPlaySessionInProgress();
	if (bPie)
	{
		NotePieStart();
	}
	else
	{
		PieSince = 0.0;
		bPieByInput = false;
	}
	if (bPie != bLastReportedPie || NowUnix() - LastPieReportTime >= PIE_REPORT_INTERVAL_SECONDS)
	{
		WritePieReport(bPie);
	}
}

void FOrchestratorStatus::WritePieReport(bool bPie)
{
	double Now = NowUnix();
	FString Path = GetPieReportPath();
	FString Temporary = Path + TEXT(".tmp");
	FString Body = FString::Printf(TEXT("{\"pid\":%u,\"pie\":%s,\"at\":%.3f,\"pieSince\":%.3f,\"pieByInput\":%s}"), FPlatformProcess::GetCurrentProcessId(),
		bPie ? TEXT("true") : TEXT("false"), Now, PieSince, bPieByInput ? TEXT("true") : TEXT("false"));
	if (FFileHelper::SaveStringToFile(Body, *Temporary) && IFileManager::Get().Move(*Path, *Temporary, true, true, false, true))
	{
		bLastReportedPie = bPie;
		LastPieReportTime = Now;
	}
}

bool FOrchestratorStatus::Tick(float DeltaTime)
{
	ReadStatusFile(false);
	if (NowUnix() - LastManagerPoll >= MANAGER_POLL_SECONDS)
	{
		LastManagerPoll = NowUnix();
		ReadManagerFile();
	}
	ReportPieState();
	// With no agent running, nothing rewrites the feed; one status call restarts the daemon that keeps it fresh.
	if (IsStale() && !bCommandRunning && NowUnix() - LastKickTime > KICK_INTERVAL_SECONDS)
	{
		RefreshNow();
	}
	return true;
}

void FOrchestratorStatus::RefreshNow()
{
	if (bCommandRunning)
	{
		return;
	}
	LastKickTime = NowUnix();
	RunOrch(TEXT("status --history 0"), false);
}

void FOrchestratorStatus::ReadStatusFile(bool bForce)
{
	FString Path = GetStatusFilePath();
	FFileStatData Stat = IFileManager::Get().GetStatData(*Path);
	if (!Stat.bIsValid)
	{
		return;
	}
	if (!bForce && Stat.ModificationTime == LastFileStamp && Stat.FileSize == LastFileSize)
	{
		return;
	}
	FString Json;
	if (!FFileHelper::LoadFileToString(Json, *Path))
	{
		return;
	}
	FOrchestratorSnapshot Parsed;
	if (!Parse(Json, Parsed))
	{
		// Caught between the orchestrator's write and its replace; the next poll reads the finished file.
		return;
	}
	LastFileStamp = Stat.ModificationTime;
	LastFileSize = Stat.FileSize;
	Snapshot = MoveTemp(Parsed);
	OnUpdated.Broadcast();
	if (!bHadFirstRead)
	{
		bHadFirstRead = true;
		OnFirstRead.Broadcast();
	}
}

FString FOrchestratorStatus::GetManagerLivePath() const
{
	return FPaths::Combine(FPlatformMisc::GetEnvironmentVariable(TEXT("LOCALAPPDATA")), OrchestratorStatusPrivate::MANAGER_LIVE_FILE);
}

bool FOrchestratorStatus::HasManager() const
{
	return ManagerFileStamp.GetTicks() > 0 && (FDateTime::UtcNow() - ManagerFileStamp).GetTotalSeconds() < MANAGER_STALE_SECONDS;
}

/** Rows are rebuilt only when what they show changes, so the second-by-second rewrite of the file costs nothing on screen. */
void FOrchestratorStatus::ReadManagerFile()
{
	FString Path = GetManagerLivePath();
	FFileStatData Stat = IFileManager::Get().GetStatData(*Path);
	bool bChanged = false;
	if (Stat.bIsValid && Stat.ModificationTime != ManagerFileStamp)
	{
		FString Json;
		TArray<FManagerAgent> Parsed;
		// A read caught between the manager's write and its replace fails to parse; the next poll reads the finished file.
		if (FFileHelper::LoadFileToString(Json, *Path) && ParseManager(Json, Parsed))
		{
			ManagerFileStamp = Stat.ModificationTime;
			FString Signature;
			for (const FManagerAgent& Agent : Parsed)
			{
				Signature += FString::Printf(TEXT("%s|%d|%s|%s|%s;"), *Agent.Key, Agent.Number, *Agent.Name, *Agent.State, *Agent.Reason);
			}
			bChanged = Signature != ManagerSignature;
			ManagerSignature = MoveTemp(Signature);
			ManagerAgents = MoveTemp(Parsed);
		}
	}
	bool bLive = HasManager();
	if (!bLive && ManagerAgents.Num() > 0)
	{
		ManagerAgents.Reset();
		ManagerSignature.Reset();
	}
	if (bChanged || bLive != bManagerWasLive)
	{
		bManagerWasLive = bLive;
		OnUpdated.Broadcast();
	}
}

/** Running agents only, the Master Agent first and the rest by the manager's card numbers. */
bool FOrchestratorStatus::ParseManager(const FString& Json, TArray<FManagerAgent>& Out)
{
	using namespace OrchestratorStatusPrivate;

	TSharedPtr<FJsonObject> Root;
	if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), Root) || !Root.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Agents = nullptr;
	if (!Root->TryGetArrayField(TEXT("agents"), Agents))
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Agents)
	{
		TSharedPtr<FJsonObject> Object = Value->AsObject();
		if (!Object.IsValid() || !Bool(Object, TEXT("isAlive")) || Str(Object, TEXT("state")) == MANAGER_CLOSED_STATE)
		{
			continue;
		}
		FManagerAgent& Agent = Out.AddDefaulted_GetRef();
		Agent.Key = Str(Object, TEXT("key"));
		Agent.Name = Str(Object, TEXT("name"));
		Agent.Kind = Str(Object, TEXT("kind"));
		Agent.State = Str(Object, TEXT("state"));
		Agent.Reason = Str(Object, TEXT("reason"));
		Agent.Number = static_cast<int32>(Num(Object, TEXT("number")));
	}
	Out.StableSort([](const FManagerAgent& A, const FManagerAgent& B)
	{
		if (A.IsMasterAgent() != B.IsMasterAgent())
		{
			return A.IsMasterAgent();
		}
		return A.Number < B.Number;
	});
	return true;
}

bool FOrchestratorStatus::Parse(const FString& Json, FOrchestratorSnapshot& Out) const
{
	using namespace OrchestratorStatusPrivate;

	TSharedPtr<FJsonObject> Root;
	if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Json), Root) || !Root.IsValid())
	{
		return false;
	}

	Out.bValid = true;
	Out.GeneratedAt = Num(Root, TEXT("generatedAt"));

	const TSharedPtr<FJsonObject>* Daemon = nullptr;
	if (Root->TryGetObjectField(TEXT("daemon"), Daemon))
	{
		Out.bDaemonAlive = Bool(*Daemon, TEXT("alive"));
	}

	const TSharedPtr<FJsonObject>* Hold = nullptr;
	if (Root->TryGetObjectField(TEXT("hold"), Hold))
	{
		Out.Hold.bOn = Bool(*Hold, TEXT("on"));
		Out.Hold.Reason = Str(*Hold, TEXT("reason"));
		Out.Hold.Since = Num(*Hold, TEXT("since"));
		Out.Hold.PausedNames = Strings(*Hold, TEXT("pausedNames"));
	}

	const TSharedPtr<FJsonObject>* Lane = nullptr;
	if (Root->TryGetObjectField(TEXT("lane"), Lane))
	{
		Out.Lane = ParseGrant(*Lane);
	}
	ParseGrants(Root, TEXT("queue"), Out.Queue);
	Out.Occupants = Strings(Root, TEXT("occupants"));

	const TArray<TSharedPtr<FJsonValue>>* Agents = nullptr;
	if (Root->TryGetArrayField(TEXT("agents"), Agents))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Agents)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			if (!Value->TryGetObject(Object))
			{
				continue;
			}
			FOrchestratorAgent& Agent = Out.Agents.AddDefaulted_GetRef();
			Agent.Owner = Str(*Object, TEXT("owner"));
			Agent.SessionId = Str(*Object, TEXT("sessionId"));
			Agent.DisplayName = Str(*Object, TEXT("displayName"));
			Agent.Cwd = Str(*Object, TEXT("cwd"));
			Agent.Transcript = Str(*Object, TEXT("transcript"));
			Agent.State = Str(*Object, TEXT("state"));
			Agent.StateText = Str(*Object, TEXT("stateText"));
			Agent.OpenTool = Str(*Object, TEXT("openTool"));
			Agent.LastActive = Num(*Object, TEXT("lastActive"));
			Agent.OpenSince = Num(*Object, TEXT("openSince"));
			Agent.bWorking = Bool(*Object, TEXT("working"));
			Agent.bWaitingOnUser = Bool(*Object, TEXT("waitingOnUser"));
			Agent.bHoldsLane = Bool(*Object, TEXT("holdsLane"));
			Agent.bPaused = Bool(*Object, TEXT("paused"));
			Agent.Grants = Strings(*Object, TEXT("grants"));
		}
	}

	const TArray<TSharedPtr<FJsonValue>>* Tasks = nullptr;
	if (Root->TryGetArrayField(TEXT("tasks"), Tasks))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Tasks)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			if (!Value->TryGetObject(Object))
			{
				continue;
			}
			FOrchestratorTask& Task = Out.Tasks.AddDefaulted_GetRef();
			Task.Id = Str(*Object, TEXT("id"));
			Task.OwnerName = Str(*Object, TEXT("ownerName"));
			Task.bOwnerAlive = Bool(*Object, TEXT("ownerAlive"));
			Task.Title = Str(*Object, TEXT("title"));
			Task.State = Str(*Object, TEXT("state"));
			Task.NextStep = Str(*Object, TEXT("nextStep"));
			Task.BlockedOn = Str(*Object, TEXT("blockedOn"));
			Task.Why = Str(*Object, TEXT("why"));
		}
	}
	Out.TasksLeftOver = static_cast<int32>(Num(Root, TEXT("tasksLeftOver")));

	const TArray<TSharedPtr<FJsonValue>>* Editors = nullptr;
	if (Root->TryGetArrayField(TEXT("editors"), Editors))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Editors)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			if (!Value->TryGetObject(Object))
			{
				continue;
			}
			FOrchestratorEditor& Editor = Out.Editors.AddDefaulted_GetRef();
			Editor.Pid = static_cast<uint32>(Num(*Object, TEXT("pid")));
			Editor.Role = Str(*Object, TEXT("role"));
			Editor.OnItName = Str(*Object, TEXT("onItName"));
			Editor.NotRespondingSince = Num(*Object, TEXT("notRespondingSince"));
			Editor.bFrozen = Bool(*Object, TEXT("frozen"));
			bool bPie = false;
			if ((*Object)->TryGetBoolField(TEXT("pie"), bPie))
			{
				Editor.Pie = bPie;
			}
		}
	}

	const TSharedPtr<FJsonObject>* Summary = nullptr;
	if (Root->TryGetObjectField(TEXT("summary"), Summary))
	{
		Out.Summary.State = Str(*Summary, TEXT("state"));
		Out.Summary.Dot = Str(*Summary, TEXT("dot"));
		Out.Summary.Text = Str(*Summary, TEXT("text"));
		Out.Summary.PiePid = static_cast<uint32>(Num(*Summary, TEXT("piePid")));
		Out.Summary.TakePauses = Strings(*Summary, TEXT("takePauses"));
	}
	return true;
}

bool FOrchestratorStatus::IsTakeArmed() const
{
	return Snapshot.Summary.TakePauses.Num() > 0 && TakeArmedAt > 0.0 && FPlatformTime::Seconds() - TakeArmedAt <= TAKE_CONFIRM_SECONDS;
}

bool FOrchestratorStatus::RequestTakeEditor(const FString& Reason)
{
	if (bCommandRunning)
	{
		return false;
	}
	if (DecideTakeClick(Snapshot.Summary.TakePauses.Num() > 0, FPlatformTime::Seconds(), TakeArmedAt) != EOrchestratorTakeClick::Take)
	{
		return false;
	}
	TakeEditor(Reason);
	return true;
}

EOrchestratorTakeClick FOrchestratorStatus::DecideTakeClick(bool bWouldPauseSomeone, double Now, double& InOutArmedAt)
{
	if (!bWouldPauseSomeone)
	{
		InOutArmedAt = 0.0;
		return EOrchestratorTakeClick::Take;
	}
	if (InOutArmedAt <= 0.0 || Now - InOutArmedAt > TAKE_CONFIRM_SECONDS)
	{
		InOutArmedAt = Now;
		return EOrchestratorTakeClick::Arm;
	}
	// The second half of a double click is the same stray click.
	if (Now - InOutArmedAt < TAKE_CONFIRM_MIN_SECONDS)
	{
		return EOrchestratorTakeClick::Ignore;
	}
	InOutArmedAt = 0.0;
	return EOrchestratorTakeClick::Take;
}

void FOrchestratorStatus::TakeEditor(const FString& Reason)
{
	// Only a non-implicit lane grant can own a PIE; an implicit one is a single command that ends on its own.
	// This editor's PIE is the agent's only when the agent is on this editor and started it: the user may be playing their own during an agent's build.
	bool bStoppedPie = false;
	bool bIsHoldersPie = Snapshot.Lane.IsSet() && !Snapshot.Lane->bImplicit && Snapshot.Lane->bOwnsPie && Snapshot.Lane->EditorPid == FPlatformProcess::GetCurrentProcessId();
	if (bStopAgentPieOnTake && GEditor && GEditor->IsPlaySessionInProgress() && bIsHoldersPie)
	{
		GEditor->RequestEndPlayMap();
		bStoppedPie = true;
	}
	FString Args = FString::Printf(TEXT("hold on --source editor --by \"%s\""), FPlatformProcess::UserName());
	FString CleanReason = Reason.TrimStartAndEnd().Replace(TEXT("\""), TEXT("'"));
	if (!CleanReason.IsEmpty())
	{
		Args += FString::Printf(TEXT(" --reason \"%s\""), *CleanReason);
	}
	if (bStoppedPie)
	{
		Args += TEXT(" --stopped-pie");
	}
	RunOrch(Args, true);
}

void FOrchestratorStatus::ReleaseEditor()
{
	if (!bCommandRunning)
	{
		RunOrch(TEXT("hold off --source editor"), true);
	}
}

void FOrchestratorStatus::RunOrch(const FString& Args, bool bReportFailure)
{
	bCommandRunning = true;
	FString Params = GetOrchParams(Args);
	FString WorkingDir = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
	TWeakPtr<FOrchestratorStatus> WeakThis = AsShared();
	Async(EAsyncExecution::Thread, [Params, WorkingDir, WeakThis, bReportFailure]()
	{
		int32 ReturnCode = -1;
		FString StdOut;
		FString StdErr;
		bool bLaunched = FPlatformProcess::ExecProcess(OrchestratorStatusPrivate::COMMAND_SHELL, *Params, &ReturnCode, &StdOut, &StdErr, *WorkingDir);
		FString Output = bLaunched ? (StdErr.IsEmpty() ? StdOut : StdErr).TrimStartAndEnd() : FString(TEXT("Could not start orch.cmd."));
		int32 Code = bLaunched ? ReturnCode : -1;
		AsyncTask(ENamedThreads::GameThread, [WeakThis, bReportFailure, Code, Output]()
		{
			TSharedPtr<FOrchestratorStatus> This = WeakThis.Pin();
			if (!This.IsValid())
			{
				return;
			}
			This->bCommandRunning = false;
			This->LastError = Code == 0 ? FString() : Output;
			This->ReadStatusFile(true);
			if (Code != 0 && bReportFailure)
			{
				FNotificationInfo Info(FText::Format(LOCTEXT("OrchFailed", "Agent orchestrator: {0}"), FText::FromString(Output.Left(400))));
				Info.ExpireDuration = OrchestratorStatusPrivate::ERROR_TOAST_SECONDS;
				Info.bUseSuccessFailIcons = true;
				if (TSharedPtr<SNotificationItem> Item = FSlateNotificationManager::Get().AddNotification(Info))
				{
					Item->SetCompletionState(SNotificationItem::CS_Fail);
				}
			}
		});
	});
}

#undef LOCTEXT_NAMESPACE
