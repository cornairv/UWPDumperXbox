#include <Windows.h>
#include <iostream>

// Test program that just waits

int main()
{
    std::cout << "UWPDumperTest " << GetCurrentProcessId() << std::endl;
    std::cout << "[enter]" << std::endl;
    (void)getchar();
    return 0;
}