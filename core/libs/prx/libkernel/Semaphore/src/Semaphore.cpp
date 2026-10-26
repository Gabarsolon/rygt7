#include "prx/libkernel/Semaphore/include/Semaphore.hpp"
#include "prx/libkernel/Time/include/Time.hpp"
#include <chrono>
#include <cstdlib>

#include <stdexcept>
#include <string>
#include <utility>

KernelSemaPrivate::KernelSemaPrivate(std::int32_t initCount, std::int32_t maxCount, std::string name, bool isFifo)
 : name(std::move(name)), tokenCount(initCount), maxCount(maxCount), isFifo(isFifo), initCount(initCount) {
}

// APS5_SEMA_WAKE_ALL=1: every waiter is woken on each signal, as before.
static bool SemaWakeAll() {
 static const bool all = std::getenv("APS5_SEMA_WAKE_ALL") != nullptr;
 return all;
}

extern "C" {

int APS5_VABI sceKernelCreateSema(KernelSema* sem, const char* name, uint32_t attr, int init, int max, void* opt) {
 (void)opt;
 if (sem == nullptr || name == nullptr || attr > 2 || init < 0 || max <= 0 || init > max) {
  APS5_INVALID_ARG_EX;
 }

 *sem = new KernelSemaPrivate(init, max, std::string(name), attr == 1);
 return KERNEL_SEMA_OK;
}

int APS5_VABI sceKernelPollSema(KernelSema sem, int need) {
 if (sem == nullptr || need <= 0) {
  APS5_INVALID_ARG_EX;
 }

 std::lock_guard<std::mutex> lock(sem->mutex);
 if (sem->tokenCount < need) {
  return SCE_KERNEL_ERROR_EBUSY;
 }
 sem->tokenCount -= need;
 return KERNEL_SEMA_OK;
}

int APS5_VABI sceKernelSignalSema(KernelSema sem, int count) {
 if (sem == nullptr || count <= 0) {
  APS5_INVALID_ARG_EX;
 }

 std::lock_guard<std::mutex> lock(sem->mutex);
 if (sem->tokenCount + count > sem->maxCount) {
  return SCE_KERNEL_ERROR_EINVAL;
 }
 sem->tokenCount += count;
 // A waiter per token (GT7's job workers wait for one each): waking every waiter for one token
 // had the whole pool contend for it. A waiter for several tokens gets everyone woken.
 if (SemaWakeAll() || sem->bulkWaiters > 0) {
  sem->condition.NotifyAll();
 } else {
  for (int i = 0; i < count && i < sem->waiterCount; ++i) sem->condition.NotifyOne();
 }
 return KERNEL_SEMA_OK;
}

int APS5_VABI sceKernelWaitSema(KernelSema sem, int need, KernelUseconds* time) {
 if (sem == nullptr || need <= 0) {
  APS5_INVALID_ARG_EX;
 }

 std::unique_lock<std::mutex> lock(sem->mutex);
 ++sem->waiterCount;
 if (need > 1) ++sem->bulkWaiters;
 struct WaiterGuard {
  KernelSemaPrivate* sem;
  int need;
  ~WaiterGuard() {
   --sem->waiterCount;
   if (need > 1) --sem->bulkWaiters;
   if (SemaWakeAll() || sem->deleted) {
    sem->condition.NotifyAll(); // DeleteSema waits for the last waiter
   } else if (sem->tokenCount > 0 && sem->waiterCount > 0) {
    // Tokens a woken waiter left (it timed out, or another thread took its token first and more
    // came) go to the next one.
    sem->condition.NotifyOne();
   }
  }
 } waiterGuard{sem, need};
 const auto generation = sem->cancelGeneration;

 const auto waitStart = std::chrono::steady_clock::now();
 const auto traceWait = [&](bool timedOut) {
  KernelTraceWait_nid_postfix("sema", __builtin_return_address(0), static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - waitStart).count()), timedOut);
 };
 if (time == nullptr) {
  sem->condition.Wait(lock, [&] { return sem->tokenCount >= need || sem->deleted || sem->cancelGeneration != generation; });
  traceWait(false);
  if (sem->deleted) {
   return SCE_KERNEL_ERROR_EACCES;
  }
  if (sem->cancelGeneration != generation) {
   return SCE_KERNEL_ERROR_ECANCELED;
  }
  sem->tokenCount -= need;
  return KERNEL_SEMA_OK;
 }

 const bool acquired = sem->condition.WaitUntil(lock, TimedWait::DeadlineNanos(*time), [&] { return sem->tokenCount >= need || sem->deleted || sem->cancelGeneration != generation; });
 traceWait(!acquired);
 if (sem->deleted) {
  return SCE_KERNEL_ERROR_EACCES;
 }
 if (sem->cancelGeneration != generation) {
  return SCE_KERNEL_ERROR_ECANCELED;
 }
 if (!acquired) {
  return SCE_KERNEL_ERROR_ETIMEDOUT;
 }
 sem->tokenCount -= need;
 return KERNEL_SEMA_OK;
}

int APS5_VABI sceKernelCancelSema(KernelSema sem, int count, int* threads) {
 if (sem == nullptr) {
  APS5_INVALID_ARG_EX;
 }
 std::lock_guard<std::mutex> lock(sem->mutex);
 if (count > sem->maxCount) {
  return SCE_KERNEL_ERROR_EINVAL;
 }
 // Every current waiter returns ECANCELED; the count becomes `count`, or the initial count for -1.
 if (threads) *threads = sem->waiterCount;
 sem->tokenCount = count < 0 ? sem->initCount : count;
 ++sem->cancelGeneration;
 sem->condition.NotifyAll();
 return KERNEL_SEMA_OK;
}

int APS5_VABI sceKernelDeleteSema(KernelSema sem) {
 if (sem == nullptr) {
  APS5_INVALID_ARG_EX;
 }

 std::unique_lock<std::mutex> lock(sem->mutex);
 sem->deleted = true;
 sem->condition.NotifyAll();
 sem->condition.Wait(lock, [&] { return sem->waiterCount == 0; });
 lock.unlock();
 delete sem;
 return KERNEL_SEMA_OK;
}

}
