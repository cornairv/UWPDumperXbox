// force strings into .text section after code
#pragma const_seg(".z.rdata")
#pragma comment(linker, "/MERGE:.z.rdata=.text")

namespace Include
{
	#include <Windows.h>
}

using Include::intptr_t;
using Include::HMODULE;
using Include::HANDLE;
using Include::DWORD;

#define CAST_FUNC(ptr, name) (reinterpret_cast<decltype(&Include::name)>(ptr))
#define IMPORT(hLib, name) auto name = CAST_FUNC(GetProcAddress(hLib, #name), name); if (!name) return __LINE__;
#define GOTO_ERROR { errorLine = __LINE__; goto cleanup; }
#define CHECK(cond) if (!(cond)) GOTO_ERROR
#define SZ_8M (1024 * 1024 * 8)

typedef intptr_t(*FunctionCall)(...);

struct SharedMemoryData
{
	DWORD lastError;
	bool stop;
	intptr_t returnValue;
	intptr_t functionAddress;
	intptr_t args[16];
	char buffer[SZ_8M];
};

struct EntryPointArgs
{
	intptr_t hKernel32;
	intptr_t GetProcAddress;
	intptr_t sharedMemorySize;
	wchar_t sharedMemoryName[64];
	wchar_t callEventName[64];
	wchar_t returnEventName[64];
};

extern "C" int EntryPoint(EntryPointArgs* args)
{
	auto hKernel32 = reinterpret_cast<HMODULE>(args->hKernel32);
	auto GetProcAddress = CAST_FUNC(args->GetProcAddress, GetProcAddress);
	IMPORT(hKernel32, OpenFileMappingW);
	IMPORT(hKernel32, MapViewOfFile);
	IMPORT(hKernel32, UnmapViewOfFile);
	IMPORT(hKernel32, OpenEventW);
	IMPORT(hKernel32, SetEvent);
	IMPORT(hKernel32, WaitForSingleObject);
	IMPORT(hKernel32, CloseHandle);
	IMPORT(hKernel32, GetLastError);
	IMPORT(hKernel32, SetLastError);
	
	int errorLine = 0;
	bool setResult;
	HANDLE hCallEvent = nullptr;
	HANDLE hReturnEvent = nullptr;
	HANDLE hSharedMemory = nullptr;
	SharedMemoryData* sharedMemory = nullptr;

	hCallEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, args->callEventName);
	CHECK(hCallEvent);

	hReturnEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, args->returnEventName);
	CHECK(hReturnEvent);

	hSharedMemory = OpenFileMappingW(FILE_MAP_WRITE, FALSE, args->sharedMemoryName);
	CHECK(hSharedMemory);

	sharedMemory = static_cast<SharedMemoryData*>(MapViewOfFile(hSharedMemory, FILE_MAP_WRITE, 0, 0, args->sharedMemorySize));
	CHECK(sharedMemory);

	sharedMemory->returnValue = reinterpret_cast<intptr_t>(sharedMemory);
	setResult = SetEvent(hReturnEvent);
	CHECK(setResult);

	while (true)
	{
		auto waitResult = WaitForSingleObject(hCallEvent, INFINITE);
		CHECK(waitResult == WAIT_OBJECT_0);

		if (sharedMemory->stop)
		{
			break;
		}

		// TODO: support floating point args
		SetLastError(ERROR_SUCCESS);
		auto function = reinterpret_cast<FunctionCall>(sharedMemory->functionAddress);
		sharedMemory->returnValue = function(
			sharedMemory->args[0],
			sharedMemory->args[1],
			sharedMemory->args[2],
			sharedMemory->args[3],
			sharedMemory->args[4],
			sharedMemory->args[5],
			sharedMemory->args[6],
			sharedMemory->args[7],
			sharedMemory->args[8],
			sharedMemory->args[9],
			sharedMemory->args[10],
			sharedMemory->args[11],
			sharedMemory->args[12],
			sharedMemory->args[13],
			sharedMemory->args[14],
			sharedMemory->args[15]);
		sharedMemory->lastError = GetLastError();

		setResult = SetEvent(hReturnEvent);
		CHECK(setResult);
	}

cleanup:
	if (hCallEvent) CloseHandle(hCallEvent);
	if (hReturnEvent) CloseHandle(hReturnEvent);
	if (sharedMemory) UnmapViewOfFile(sharedMemory);
	if (hSharedMemory) CloseHandle(hSharedMemory);
	return errorLine;
}